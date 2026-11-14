using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Persistence;

/// <summary>
/// One-shot migration mode (<c>Gozleme.Api migrate</c>). Runs as the owner role
/// (<c>ConnectionStrings:GozlemeMigrator</c>) so the serving process never holds DDL rights.
/// </summary>
internal static partial class MigrationRunner
{
    public const string MigratorConnectionStringName = "GozlemeMigrator";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.UseUtcTimestamp = true);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        // EF probes for the history table before creating it on a fresh database and logs that as an error.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Critical);

        var connectionString = new NpgsqlConnectionStringBuilder(
            builder.Configuration.GetConnectionString(MigratorConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{MigratorConnectionStringName}' is not configured."))
        {
            PersistSecurityInfo = false,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
        builder.Services.AddDbContext<GozlemeDbContext>(options => options.UseNpgsql(connectionString));

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MigrationRunner));
        var attempts = builder.Configuration.GetValue("Gozleme:Migrate:MaxAttempts", 30);
        var delay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Gozleme:Migrate:RetryDelaySeconds", 2));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GozlemeDbContext>();
                await db.Database.MigrateAsync();
                var latest = (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault() ?? "none";
                LogMigrated(logger, latest);
                return 0;
            }
            catch (NpgsqlException ex) when (attempt < attempts && ex is not PostgresException)
            {
                LogRetry(logger, attempt, attempts, ex.GetType().Name);
                await Task.Delay(delay);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrated; latest migration {MigrationId}")]
    private static partial void LogMigrated(ILogger logger, string migrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not ready (attempt {Attempt}/{MaxAttempts}, {ExceptionType}); retrying")]
    private static partial void LogRetry(ILogger logger, int attempt, int maxAttempts, string exceptionType);
}
