using System.Diagnostics;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Status;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// Foundational observability (GOZ-0000-IF-11, FR-020–FR-025): one trace per status request
/// spanning server → probe → PostgreSQL, propagated context, outcome metrics, and no secrets.
/// </summary>
public sealed class TelemetryTests : IAsyncLifetime
{
    private static readonly string[] FixedProbeMessages = ["Database unreachable", "Database schema not initialized"];

    private GozlemeDatabase _db = null!;
    private GozlemeApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _factory = new GozlemeApiFactory(_db.AppConnectionString);
        (_client, _) = await AuthClient.SignedInAsync(_factory);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Status_request_forms_one_trace_from_propagated_context_through_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/status", UriKind.Relative));
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var spans = await WaitForSpansAsync(s => s.TraceId == traceId && s.Kind == ActivityKind.Server, ct);
        var trace = spans.Where(s => s.TraceId == traceId).ToArray();

        var server = Assert.Single(trace, s => s.Kind == ActivityKind.Server);
        Assert.Equal("/api/v1/status", server.GetTagItem("url.path"));
        var probe = Assert.Single(trace, s => s.Source.Name == "Gozleme.Api" && s.DisplayName == "status.probe database");
        Assert.Equal(server.SpanId, probe.ParentSpanId);
        Assert.Equal("healthy", probe.GetTagItem("gozleme.status.outcome"));
        // Npgsql emits a connection-open span and a command span under the probe. Session validation
        // (GOZ-0001) adds its own queries directly under the server span; nothing else is parented elsewhere.
        Assert.All(trace.Where(s => s.Source.Name == "Npgsql"), s => Assert.Contains(s.ParentSpanId, new[] { probe.SpanId, server.SpanId }));
        var npgsql = trace.Where(s => s.Source.Name == "Npgsql" && s.ParentSpanId == probe.SpanId).ToArray();
        var command = Assert.Single(npgsql, s => !s.DisplayName.StartsWith("CONNECT", StringComparison.Ordinal));
        Assert.Equal("postgresql", command.GetTagItem("db.system") ?? command.GetTagItem("db.system.name"));
    }

    [Fact]
    public async Task Health_probes_are_not_traced()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct)).Dispose();
        (await _client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), ct)).Dispose();
        var spans = await WaitForSpansAsync(s => s.Kind == ActivityKind.Server, ct);
        Assert.DoesNotContain(spans, s => s.Kind == ActivityKind.Server && (s.GetTagItem("url.path") as string)?.StartsWith("/health", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Outage_marks_probe_span_as_error_and_counts_outcomes_without_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), ct)).Dispose();
        await _db.StopAsync();
        // GOZ-0001 fails closed on /api/v1/status while the database is down (FR-021a) and probe
        // requests are not traced, so exercise the same probe directly as a root span.
        await _factory.Services.GetRequiredService<IDatabaseProbe>().ProbeAsync(ct);
        await _db.StartAgainAsync();

        var spans = await WaitForSpansAsync(s => s.DisplayName == "status.probe database" && s.Status == ActivityStatusCode.Error, ct);
        // Timing on slower hosts can yield more than one failed probe around the stop; each must carry
        // only the fixed, non-sensitive outcome and message.
        var failed = spans.Where(s => s.DisplayName == "status.probe database" && s.Status == ActivityStatusCode.Error).ToArray();
        Assert.NotEmpty(failed);
        Assert.All(failed, s =>
        {
            Assert.Equal("unavailable", s.GetTagItem("gozleme.status.outcome"));
            Assert.Contains(s.StatusDescription, FixedProbeMessages);
        });
        Assert.Contains(failed, s => s.StatusDescription == "Database unreachable");

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var outcomes = new Dictionary<string, long>();
        foreach (var metric in metrics.Where(m => m.Name == "gozleme.status.checks"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    if (tag.Key == "gozleme.status.outcome")
                    {
                        outcomes[(string)tag.Value!] = Math.Max(outcomes.GetValueOrDefault((string)tag.Value!), point.GetSumLong());
                    }
                }
            }
        }

        Assert.True(outcomes.GetValueOrDefault("healthy") >= 1, "healthy outcome counted");
        Assert.True(outcomes.GetValueOrDefault("unavailable") >= 1, "unavailable outcome counted");

        string[] forbidden = [GozlemeDatabase.AppPassword, GozlemeDatabase.MigratorPassword, GozlemeDatabase.SuperuserPassword, "Password=", "password="];
        var exported = string.Join('\n', _factory.SpansSnapshot().SelectMany(s =>
            s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
                .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))));
        foreach (var secret in forbidden)
        {
            Assert.DoesNotContain(secret, exported, StringComparison.Ordinal);
        }

        // Metric labels must not carry connection-string material (Npgsql pool names default to it).
        var labels = new List<string>();
        foreach (var metric in metrics)
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    labels.Add($"{tag.Key}={tag.Value}");
                }
            }
        }

        Assert.Contains("db.client.connection.pool.name=gozleme-api", labels);
        foreach (var fragment in forbidden.Concat(["Host=", "Username="]))
        {
            Assert.DoesNotContain(labels, l => l.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private async Task<Activity[]> WaitForSpansAsync(Func<Activity, bool> until, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var spans = _factory.SpansSnapshot();
            if (spans.Any(until) || DateTime.UtcNow > deadline)
            {
                return spans;
            }

            await Task.Delay(100, ct);
        }
    }
}
