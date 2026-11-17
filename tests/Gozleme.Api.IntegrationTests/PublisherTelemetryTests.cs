using System.Diagnostics;
using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 FR-026, SC-008 (research R10): publisher and capability writes emit one span each and the bounded
/// <c>gozleme.publisher.operations</c> counter; audit 1401–1405 carry opaque ids only; no handle, capability name,
/// alias, display name, email, signed context or key reaches spans, metric labels or logs.
/// </summary>
public sealed class PublisherTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] CounterLabelKeys = ["gozleme.publisher.operation", "gozleme.publisher.outcome"];
    private static readonly string[] SpanTagKeys = ["gozleme.organization.id", "gozleme.publisher.id", "gozleme.capability.id", "gozleme.publisher.outcome"];
    private static readonly string[] Operations = ["register", "rename", "capability_create", "capability_rename"];
    private static readonly string[] Outcomes = ["success", "unchanged", "validation_failed", "handle_unavailable", "conflict", "forbidden", "suspended", "not_found", "rate_limited"];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Writes_are_observable_with_bounded_labels_and_no_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, aliceEmail) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        const string orgName = "Telemetry Secret Org";
        var org = await OrgClient.CreateAsync(alice, orgName);
        var (carol, carolId) = await TwoTenants.AddPublisherMemberAsync(_factory, alice, Guid.Parse(org));
        using var _c = carol;
        var aliceId = await OrgClient.UserIdAsync(alice);

        var handle = OrgClient.NewHandle("telem");
        const string publisherName = "Secret Publisher Display";
        const string renamedPublisher = "Secret Publisher Renamed";
        const string capabilityName = "secret-capability";
        const string capabilityDisplay = "Secret Capability Display";
        var publisher = (await OrgClient.RegisterPublisherAsync(alice, org, handle, publisherName)).GetProperty("id").GetString()!;
        Expect(HttpStatusCode.Conflict, await OrgClient.RegisterPublisherRawAsync(alice, org, handle)); // handle_unavailable + 1405
        Expect(HttpStatusCode.BadRequest, await OrgClient.RegisterPublisherRawAsync(alice, org, "Not-Valid")); // validation_failed
        Expect(HttpStatusCode.Forbidden, await OrgClient.RegisterPublisherRawAsync(carol, org, OrgClient.NewHandle("telem"))); // forbidden
        Expect(HttpStatusCode.OK, await OrgClient.RenamePublisherAsync(alice, org, publisher, new { displayName = publisherName })); // unchanged
        Expect(HttpStatusCode.OK, await OrgClient.RenamePublisherAsync(alice, org, publisher, new { displayName = renamedPublisher })); // success + 1402
        Expect(HttpStatusCode.NotFound, await OrgClient.RenamePublisherAsync(alice, org, Guid.CreateVersion7().ToString(), new { displayName = "X" })); // not_found
        var capability = (await OrgClient.CreateCapabilityAsync(carol, org, publisher, capabilityName, capabilityDisplay)).GetProperty("id").GetString()!;
        Expect(HttpStatusCode.Conflict, await OrgClient.CreateCapabilityRawAsync(carol, org, publisher, capabilityName)); // conflict
        Expect(HttpStatusCode.Forbidden, await OrgClient.CreateCapabilityRawAsync(alice, org, publisher, "other")); // admin lacks publisher
        Expect(HttpStatusCode.OK, await OrgClient.RenameCapabilityAsync(carol, org, publisher, capability, new { displayName = "Secret Capability Renamed" })); // success + 1404

        var spans = await WaitForAsync(
            () => _factory.SpansSnapshot(),
            s => s.Any(a => a.DisplayName == "gozleme.capability.rename" && Tag(a, "gozleme.publisher.outcome") == "success"),
            ct);
        var publisherSpans = spans.Where(s => s.DisplayName.StartsWith("gozleme.publisher.", StringComparison.Ordinal) || s.DisplayName.StartsWith("gozleme.capability.", StringComparison.Ordinal)).ToList();
        Assert.Contains(publisherSpans, s => s.DisplayName == "gozleme.publisher.register" && Tag(s, "gozleme.publisher.outcome") == "success" && Tag(s, "gozleme.publisher.id") == publisher && Tag(s, "gozleme.organization.id") == org);
        Assert.Contains(publisherSpans, s => s.DisplayName == "gozleme.publisher.register" && Tag(s, "gozleme.publisher.outcome") == "handle_unavailable" && s.Status == ActivityStatusCode.Error);
        Assert.Contains(publisherSpans, s => s.DisplayName == "gozleme.publisher.rename" && Tag(s, "gozleme.publisher.outcome") == "unchanged" && s.Status != ActivityStatusCode.Error);
        Assert.Contains(publisherSpans, s => s.DisplayName == "gozleme.capability.create" && Tag(s, "gozleme.capability.id") == capability);
        Assert.Contains(publisherSpans, s => s.DisplayName == "gozleme.capability.create" && Tag(s, "gozleme.publisher.outcome") == "forbidden");
        Assert.All(publisherSpans.Where(s => s.Source.Name.StartsWith("Gozleme", StringComparison.Ordinal)), s => Assert.All(s.TagObjects, t => Assert.Contains(t.Key, SpanTagKeys)));

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var points = Points(metrics, "gozleme.publisher.operations");
        foreach (var (operation, outcome) in new[]
                 {
                     ("register", "success"), ("register", "handle_unavailable"), ("register", "validation_failed"), ("register", "forbidden"),
                     ("rename", "unchanged"), ("rename", "success"), ("rename", "not_found"),
                     ("capability_create", "success"), ("capability_create", "conflict"), ("capability_create", "forbidden"),
                     ("capability_rename", "success"),
                 })
        {
            Assert.Contains($"gozleme.publisher.operation={operation};gozleme.publisher.outcome={outcome}", points);
        }

        var labels = points.SelectMany(p => p.Split(';')).ToList();
        Assert.All(labels, l => Assert.Contains(l.Split('=')[0], CounterLabelKeys));
        Assert.All(labels, l => Assert.Contains(l.Split('=')[1], l.StartsWith("gozleme.publisher.operation", StringComparison.Ordinal) ? Operations : Outcomes));

        var logs = string.Join('\n', _factory.Logs);
        Assert.Contains($"Gozleme.Security.Audit: Publisher registered {publisher} in {org} by {aliceId} with claim ", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Publisher registration refused (handle_unavailable) in {org} {aliceId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Publisher renamed {publisher} in {org} by {aliceId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Capability created {capability} under {publisher} in {org} by {carolId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Capability renamed {capability} under {publisher} in {org} by {carolId}", logs, StringComparison.Ordinal);
        Assert.Single(logs.Split('\n'), l => l.Contains($"Publisher renamed {publisher}", StringComparison.Ordinal)); // the unchanged rename is not audited

        // Spans are collected process-wide; keep the API's request traces (other tests' direct SQL carries literal contexts).
        var requestTraces = spans.Where(s => s.Kind == ActivityKind.Server).Select(s => s.TraceId).ToHashSet();
        var exported = string.Join('\n', spans.Where(s => requestTraces.Contains(s.TraceId)).SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)));
        foreach (var value in new[] { handle, $"{handle}/{capabilityName}", capabilityName, publisherName, renamedPublisher, capabilityDisplay, orgName, aliceEmail, "v1.", "gozleme_session=", GozlemeDatabase.ContextKey })
        {
            Assert.DoesNotContain(value, exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, logs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(points, p => p.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_rate_limited_registration_is_counted_and_audited_without_the_handle()
    {
        await using var factory = new GozlemeApiFactory(fixture.Database.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Publishers:RegisterPerHour"] = "1" });
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Telemetry Limit");
        var aliceId = await OrgClient.UserIdAsync(alice);
        await OrgClient.RegisterPublisherAsync(alice, org);
        var refused = OrgClient.NewHandle("limit");
        Expect((HttpStatusCode)429, await OrgClient.RegisterPublisherRawAsync(alice, org, refused));

        factory.FlushMetrics();
        Metric[] metrics;
        lock (factory.Metrics)
        {
            metrics = [.. factory.Metrics];
        }

        Assert.Contains("gozleme.publisher.operation=register;gozleme.publisher.outcome=rate_limited", Points(metrics, "gozleme.publisher.operations"));
        var logs = string.Join('\n', factory.Logs);
        Assert.Contains($"Gozleme.Security.Audit: Publisher registration refused (rate_limited) in {org} {aliceId}", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(refused, logs, StringComparison.Ordinal);
    }

    private static void Expect(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
        }
    }

    private static string? Tag(Activity span, string key) => span.GetTagItem(key)?.ToString();

    /// <summary>Each metric point's tags as <c>key=value;key=value</c> in key order.</summary>
    private static List<string> Points(Metric[] metrics, string name)
    {
        var points = new List<string>();
        foreach (var metric in metrics.Where(m => m.Name == name))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new List<string>();
                foreach (var tag in point.Tags)
                {
                    tags.Add($"{tag.Key}={tag.Value}");
                }

                points.Add(string.Join(';', tags.Order(StringComparer.Ordinal)));
            }
        }

        return points;
    }

    private static async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = probe();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(100, ct);
        }
    }
}
