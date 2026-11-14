using System.Text.Json;
using System.Text.Json.Serialization;
using Gozleme.Api.Status;
using Gozleme.Api.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

namespace Gozleme.Api;

/// <summary>Composes the Gözleme API host. Product modules are added by later specs.</summary>
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

        var app = builder.Build();

        // Liveness never depends on downstream services; readiness requires the database.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        app.MapGet("/api/v1/status", async (StatusService status, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await status.GetAsync(ct));
        });

        return app;
    }

    private static NpgsqlDataSource CreateDataSource(IConfiguration configuration)
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
