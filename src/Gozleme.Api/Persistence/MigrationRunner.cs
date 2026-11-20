using System.Diagnostics;
using System.Text;
using Gozleme.Api.Children;
using Gozleme.Api.Identity;
using Gozleme.Api.Publishers;
using Gozleme.Api.Telemetry;
using Gozleme.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Persistence;

/// <summary>
/// One-shot migration mode (<c>Gozleme.Api migrate</c>). Runs as the owner role
/// (<c>ConnectionStrings:GozlemeMigrator</c>) so the serving process never holds DDL rights.
/// Since GOZ-0005 it also records the registry origin once (research R6): validate the configured value,
/// compare it with any recorded value <b>before</b> migrating, migrate, record, then verify. A recorded
/// origin is never overwritten. Since GOZ-0008 its last step, after the origin is verified, gives revisions without
/// generation 1 their children and generation 1 (<see cref="ChildInstaller"/>, research R6).
/// </summary>
internal static partial class MigrationRunner
{
    public const string MigratorConnectionStringName = "GozlemeMigrator";
    public const string ServiceName = "gozleme-migrator";

    public static async Task<int> RunAsync(string[] args, ILoggerProvider? logs = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.UseUtcTimestamp = true);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs); // tests capture the audit events
        }

        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.AddGozlemeTelemetry(ServiceName);
        // EF probes for the history table before creating it on a fresh database and logs that as an error.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Critical);

        var connectionString = new NpgsqlConnectionStringBuilder(
            builder.Configuration.GetConnectionString(MigratorConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{MigratorConnectionStringName}' is not configured."))
        {
            PersistSecurityInfo = false,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
        // Named so Npgsql telemetry labels the pool by name rather than by connection string.
        var dataSource = new NpgsqlDataSourceBuilder(connectionString) { Name = ServiceName }.Build();
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddDbContext<GozlemeDbContext>(options => GozlemeDbContext.Configure(options, dataSource));
        builder.Services.AddSingleton<SecurityAudit>();

        var tenancy = builder.Configuration.GetSection(TenancyOptions.Section).Get<TenancyOptions>() ?? new TenancyOptions();
        var origin = builder.Configuration.GetSection(RegistryOriginOptions.Section).Get<RegistryOriginOptions>()?.Origin;

        using var host = builder.Build();
        await host.StartAsync(); // starts telemetry providers; disposing the host flushes exporters
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MigrationRunner));
        if (!tenancy.IsValid)
        {
            LogKeyMissing(logger, TenancyOptions.Section, TenancyOptions.MinKeyLength);
            await host.StopAsync();
            return 1;
        }

        if (!RegistryOrigin.IsValid(origin))
        {
            LogOriginInvalid(logger, RegistryOrigin.InvalidMessage);
            await host.StopAsync();
            return 1;
        }

        var attempts = builder.Configuration.GetValue("Gozleme:Migrate:MaxAttempts", 30);
        var delay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Gozleme:Migrate:RetryDelaySeconds", 2));

        for (var attempt = 1; ; attempt++)
        {
            using var activity = GozlemeTelemetry.ActivitySource.StartActivity("database.migrate");
            activity?.SetTag("gozleme.migrate.attempt", attempt);
            try
            {
                await using var scope = host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GozlemeDbContext>();
                if (await RecordedOriginAsync(db) is { } before && !string.Equals(before, origin, StringComparison.Ordinal))
                {
                    LogOriginMismatch(logger, RegistryOrigin.MismatchMessage);
                    activity?.SetStatus(ActivityStatusCode.Error, "origin_mismatch");
                    await host.StopAsync();
                    return 1; // before MigrateAsync: a mismatched run applies nothing
                }

                await db.Database.MigrateAsync();
                var latest = (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault() ?? "none";
                LogMigrated(logger, latest);
                activity?.SetTag("gozleme.migrate.latest", latest);
                var changed = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO gozleme_private.context_keys (id, key) VALUES (1, {Encoding.UTF8.GetBytes(tenancy.ContextKey!)})
                    ON CONFLICT (id) DO UPDATE SET key = EXCLUDED.key, provisioned_at = now()
                    WHERE context_keys.key IS DISTINCT FROM EXCLUDED.key
                    """);
                LogKeyProvisioned(logger, changed > 0 ? "provisioned" : "unchanged");

                var recorded = await db.Database.ExecuteSqlAsync($"INSERT INTO gozleme.registry_origin (id, origin) VALUES (1, {origin}) ON CONFLICT (id) DO NOTHING");
                if (!string.Equals(await RecordedOriginAsync(db), origin, StringComparison.Ordinal))
                {
                    LogOriginMismatch(logger, RegistryOrigin.MismatchMessage);
                    activity?.SetStatus(ActivityStatusCode.Error, "origin_mismatch");
                    await host.StopAsync();
                    return 1;
                }

                LogOriginRecorded(logger, recorded > 0 ? "recorded" : "verified");
                var installed = await ChildInstaller.RunAsync(db, scope.ServiceProvider.GetRequiredService<SecurityAudit>());
                LogChildrenInstalled(logger, installed.Revisions, installed.Capabilities);
                await host.StopAsync();
                return 0;
            }
            catch (NpgsqlException ex) when (attempt < attempts && ex is not PostgresException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                LogRetry(logger, attempt, attempts, ex.GetType().Name);
                await Task.Delay(delay);
            }
        }
    }

    /// <summary>The recorded origin, or null before the table exists or the first record.</summary>
    private static async Task<string?> RecordedOriginAsync(GozlemeDbContext db)
    {
        var exists = await db.Database.SqlQuery<bool>($"SELECT to_regclass('gozleme.registry_origin') IS NOT NULL AS \"Value\"").SingleAsync();
        return exists
            ? await db.Database.SqlQuery<string>($"SELECT origin AS \"Value\" FROM gozleme.registry_origin").SingleOrDefaultAsync()
            : null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message} Nothing migrated")]
    private static partial void LogOriginInvalid(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogOriginMismatch(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registry origin {Outcome}")]
    private static partial void LogOriginRecorded(ILogger logger, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Children installed for {RevisionCount} revisions of {CapabilityCount} capabilities")]
    private static partial void LogChildrenInstalled(ILogger logger, int revisionCount, int capabilityCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrated; latest migration {MigrationId}")]
    private static partial void LogMigrated(ILogger logger, string migrationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Section}:ContextKey must be set to at least {MinLength} characters; nothing migrated")]
    private static partial void LogKeyMissing(ILogger logger, string section, int minLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant context key {Outcome}")]
    private static partial void LogKeyProvisioned(ILogger logger, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not ready (attempt {Attempt}/{MaxAttempts}, {ExceptionType}); retrying")]
    private static partial void LogRetry(ILogger logger, int attempt, int maxAttempts, string exceptionType);
}
