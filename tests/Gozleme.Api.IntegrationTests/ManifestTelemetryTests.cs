using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gozleme.Api.IntegrationTests.Infrastructure;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 FR-022, SC-004 (research R16): one <c>gozleme.manifest.validate</c> span per request with bounded tags,
/// opaque IDs only after membership, and the counters <c>gozleme.manifest.validations</c> and
/// <c>gozleme.manifest.problems</c> with closed label sets. No document text, synthetic secret, capability name,
/// alias, native name, pointer or digest reaches spans, metric labels or logs.
/// </summary>
public sealed class ManifestTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] SpanTagKeys =
        ["gozleme.manifest.family", "gozleme.manifest.outcome", "gozleme.manifest.problem_count", "gozleme.organization.id", "gozleme.publisher.id", "gozleme.capability.id"];

    private static readonly string[] Families = ["mcp", "a2a", "api", "event", "skill", "model", "unknown"];

    private static readonly string[] Outcomes =
        ["accepted", "rejected", "forbidden", "not_found", "suspended", "rate_limited", "too_large", "unsupported_media_type", "unavailable"];

    private static readonly string[] Categories =
        ["malformed", "too_large", "unsupported_version", "unsupported_family", "unsupported_format", "unknown_field", "reserved_claim", "secret_value", "identity_mismatch", "external_reference", "invalid_value"];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "60" });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Validations_are_observable_with_bounded_labels_and_no_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _b = bob;
        var org = await OrgClient.CreateAsync(alice, "Manifest Telemetry");
        var (carol, _) = await TwoTenants.AddPublisherMemberAsync(_factory, alice, Guid.Parse(org));
        using var _c = carol;
        var handle = OrgClient.NewHandle("mtel");
        var publisherId = (await OrgClient.RegisterPublisherAsync(alice, org, handle)).GetProperty("id").GetString()!;
        const string capabilityName = "telemetry-secret-cap";
        var capability = await OrgClient.CreateCapabilityAsync(carol, org, publisherId, capabilityName);
        var capabilityId = capability.GetProperty("id").GetString()!;
        var canonical = capability.GetProperty("canonicalId").GetString()!;
        var path = ManifestClient.Validate(org, publisherId, capabilityId);

        var digests = new List<string>();
        foreach (var sample in ManifestClient.Samples)
        {
            var (status, body) = await ManifestClient.SendAsync(carol, path, sample.Body(canonical));
            Assert.Equal(HttpStatusCode.OK, status);
            if (JsonNode.Parse(body)!["native"]?["digest"] is { } digest)
            {
                digests.Add(digest.GetValue<string>()["sha256:".Length..]);
            }
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(alice, path, "{}"u8.ToArray())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ManifestClient.SendAsync(bob, path, "{}"u8.ToArray())).Status);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await ManifestClient.SendAsync(carol, path, "{}"u8.ToArray(), "text/plain")).Status);

        var spans = await WaitForAsync(
            () => _factory.SpansSnapshot().Where(s => s.DisplayName == "gozleme.manifest.validate").ToList(),
            s => s.Count >= ManifestClient.Samples.Count + 3,
            ct);
        Assert.Equal(ManifestClient.Samples.Count + 3, spans.Count);
        Assert.All(spans, s => Assert.All(s.TagObjects, t => Assert.Contains(t.Key, SpanTagKeys)));
        Assert.All(spans, s => Assert.Contains(Tag(s, "gozleme.manifest.family"), Families));
        Assert.All(spans, s => Assert.Contains(Tag(s, "gozleme.manifest.outcome"), Outcomes));
        Assert.Contains(spans, s => Tag(s, "gozleme.manifest.outcome") == "accepted" && Tag(s, "gozleme.manifest.family") == "skill" && Tag(s, "gozleme.capability.id") == capabilityId);
        Assert.Contains(spans, s => Tag(s, "gozleme.manifest.outcome") == "rejected" && Tag(s, "gozleme.manifest.problem_count") == "1");
        Assert.Contains(spans, s => Tag(s, "gozleme.manifest.outcome") == "forbidden" && s.Status == ActivityStatusCode.Error && Tag(s, "gozleme.organization.id") == org && Tag(s, "gozleme.capability.id") is null);
        var notFound = Assert.Single(spans, s => Tag(s, "gozleme.manifest.outcome") == "not_found");
        Assert.Null(Tag(notFound, "gozleme.organization.id")); // a non-member: no opaque ID before membership
        Assert.Contains(spans, s => Tag(s, "gozleme.manifest.outcome") == "unsupported_media_type");

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var validations = Points(metrics, "gozleme.manifest.validations");
        foreach (var family in new[] { "mcp", "a2a", "api", "event", "skill", "model" })
        {
            Assert.Contains($"gozleme.manifest.family={family};gozleme.manifest.outcome=accepted", validations);
            Assert.Contains($"gozleme.manifest.family={family};gozleme.manifest.outcome=rejected", validations);
        }

        Assert.Contains("gozleme.manifest.family=unknown;gozleme.manifest.outcome=forbidden", validations);
        var problems = Points(metrics, "gozleme.manifest.problems");
        foreach (var category in Categories)
        {
            Assert.Contains(problems, p => p.EndsWith($"gozleme.manifest.category={category}", StringComparison.Ordinal) || p.Contains($"gozleme.manifest.category={category};", StringComparison.Ordinal));
        }

        var labels = validations.Concat(problems).SelectMany(p => p.Split(';')).ToList();
        Assert.All(labels, l => Assert.Contains(l.Split('=')[1], l.StartsWith("gozleme.manifest.family", StringComparison.Ordinal) ? Families : l.StartsWith("gozleme.manifest.outcome", StringComparison.Ordinal) ? Outcomes : Categories));

        var requestTraces = _factory.SpansSnapshot().Where(s => s.Kind == ActivityKind.Server).Select(s => s.TraceId).ToHashSet();
        var exported = string.Join('\n', _factory.SpansSnapshot().Where(s => requestTraces.Contains(s.TraceId))
            .SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)));
        var logs = string.Join('\n', _factory.Logs);
        var secrets = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(ManifestClient.SampleDirectory, "synthetic-secrets.json")))!;
        var forbidden = secrets
            .Concat([capabilityName, $"{handle}/{capabilityName}", "io.example.demo/forecasts", "forecast-summary", "Forecasts API", "/tools/0/execution", "/remotes/0/headers/0/value", "get_forecast"])
            .Concat(digests);
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(validations.Concat(problems), p => p.Contains(value, StringComparison.Ordinal));
        }
    }

    private static string? Tag(Activity span, string key) => span.GetTagItem(key)?.ToString();

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
