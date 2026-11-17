using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API against a test database as the ordinary app role, capturing logs and telemetry.
/// <paramref name="settings"/> override configuration; auth and redemption rate limits are raised by default so only
/// rate-limit tests hit them.
/// </summary>
public sealed class GozlemeApiFactory(
    string appConnectionString,
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    /// <summary>
    /// Which factory served each request trace. Activity listeners are process-wide, so every factory's
    /// exporter also receives spans from requests that other test classes send to their own hosts.
    /// </summary>
    private static readonly ConcurrentDictionary<ActivityTraceId, GozlemeApiFactory> RequestOwners = new();

    private static readonly Dictionary<string, string?> Defaults = new(StringComparer.Ordinal)
    {
        ["Gozleme:Auth:RateLimit:SignInPerMinute"] = "1000",
        ["Gozleme:Auth:RateLimit:RegisterPerMinute"] = "1000",
        ["Gozleme:Auth:RateLimit:GlobalPerMinute"] = "100000",
        ["Gozleme:Organizations:RedeemPerMinute"] = "100",
        ["Gozleme:Rls:ContextKey"] = GozlemeDatabase.ContextKey,
        ["Gozleme:Registry:Origin"] = GozlemeDatabase.Origin,
        ["Gozleme:Publishers:RegisterPerHour"] = "100",
    };

    public ConcurrentQueue<string> Logs { get; } = new();

    public List<Activity> Activities { get; } = [];

    public List<Metric> Metrics { get; } = [];

    /// <summary>
    /// Snapshot of exported spans (the in-memory exporter appends from request threads), without spans
    /// of requests served by another factory. Spans outside any request (startup, background) are kept.
    /// </summary>
    public Activity[] SpansSnapshot()
    {
        lock (Activities)
        {
            return [.. Activities.Where(a => !RequestOwners.TryGetValue(a.TraceId, out var owner) || owner == this)];
        }
    }

    public void FlushMetrics() => Services.GetRequiredService<MeterProvider>().ForceFlush();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Gozleme", appConnectionString);
        builder.UseSetting("Gozleme:BuildVersion", "0.0.0-it");
        foreach (var (key, value) in Defaults.Concat(settings ?? new Dictionary<string, string?>()))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(new CapturingLoggerProvider(Logs)));
        builder.ConfigureServices(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(new LockedActivityExporter(Activities))));
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(Metrics));
            services.AddSingleton<IStartupFilter>(new RecordRequestOwner(this));
            configureServices?.Invoke(services);
        });
    }

    /// <summary>Records the request's trace before any of its spans can end, so ownership is never late.</summary>
    private sealed class RecordRequestOwner(GozlemeApiFactory owner) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (Activity.Current is { } activity)
                {
                    RequestOwners[activity.TraceId] = owner;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class LockedActivityExporter(List<Activity> sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            lock (sink)
            {
                foreach (var activity in batch)
                {
                    sink.Add(activity);
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
