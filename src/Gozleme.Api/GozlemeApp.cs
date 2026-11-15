using System.Text.Json;
using System.Text.Json.Serialization;
using Gozleme.Api.Identity;
using Gozleme.Api.Status;
using Gozleme.Api.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

namespace Gozleme.Api;

/// <summary>Composes the Gözleme API host: status (GOZ-0000) and local identity (GOZ-0001).</summary>
internal static class GozlemeApp
{
    public const string AppConnectionStringName = "Gozleme";
    public const string ServiceName = "gozleme-api";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddJsonConsole(options =>
        {
            options.UseUtcTimestamp = true;
            options.IncludeScopes = true; // carries TraceId/SpanId for log ↔ trace correlation
        });
        builder.AddGozlemeTelemetry(ServiceName);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => CreateDataSource(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        builder.Services.AddSingleton<StatusService>();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        builder.AddGozlemeIdentity();

        var app = builder.Build();

        // Liveness never depends on downstream services; readiness requires the database. Both are
        // answered before session resolution, so a cookie never makes a probe touch the database.
        app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.UseHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        // Client address from the trusted web tier only (rate-limit partitioning), then session resolution.
        app.UseForwardedHeaders();
        app.UseDatabaseUnavailableResponses();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapGet("/api/v1/status", async (StatusService status, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await status.GetAsync(ct));
        }).RequireAuthorization(); // FR-021: status data is for signed-in users; probes above stay anonymous

        app.MapAuthEndpoints();

        return app;
    }

    internal static NpgsqlDataSource CreateDataSource(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(AppConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{AppConnectionStringName}' is not configured.");

        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = DatabaseProbe.TimeoutSeconds,
            CommandTimeout = DatabaseProbe.TimeoutSeconds,
            PersistSecurityInfo = false,
            // Kerberos/GSS is not a supported local auth mode; skip the probe (and its native-library noise).
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        // A named data source keeps the connection string out of telemetry (pool name labels).
        return new NpgsqlDataSourceBuilder(csb.ConnectionString) { Name = ServiceName }.Build();
    }
}
