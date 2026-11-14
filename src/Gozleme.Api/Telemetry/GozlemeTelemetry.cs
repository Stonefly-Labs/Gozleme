using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Gozleme.Api.Telemetry;

/// <summary>
/// Foundational OpenTelemetry wiring (GOZ-0000-IF-11). Every Gözleme process registers through
/// <see cref="AddGozlemeTelemetry"/>; domain code records spans and metrics via
/// <see cref="ActivitySource"/> and <see cref="Meter"/>. Export is OTLP and only enabled when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, so telemetry never becomes a runtime dependency.
/// </summary>
internal static class GozlemeTelemetry
{
    public const string SourceName = "Gozleme.Api";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static string ServiceVersion(IConfiguration configuration) =>
        configuration["Gozleme:BuildVersion"] is { Length: > 0 } configured
            ? configured
            : typeof(GozlemeTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static IHostApplicationBuilder AddGozlemeTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: ServiceVersion(builder.Configuration))
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant())]))
            .WithTracing(tracing => tracing
                .AddSource(SourceName)
                .AddAspNetCoreInstrumentation(options =>
                    // Orchestrator probes poll every few seconds; tracing them only adds noise.
                    options.Filter = http => !http.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(SourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation());

        if (otlpEnabled)
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
