using System.Diagnostics;
using System.Net;
using Gozleme.Api.Children;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T060 (FR-019, research R11): <c>gozleme.children.assign</c> with the four counts and
/// <c>gozleme.projection.generation.record</c> run inside <c>gozleme.manifest.publish</c>; the counters
/// <c>gozleme.children.assignments</c> and <c>gozleme.projection.generations</c> carry closed label sets; read spans
/// carry the child and generation IDs only once the target is resolved; and no native name, title, digest, version,
/// document, capability name or alias appears in spans, metric labels or captured logs. T076: the assignment and
/// generation spans enclose their database work. T075: <c>gozleme.children.install</c> carries the revision and
/// capability counts only, and <c>gozleme.operator.projection_regenerate</c> its exit code, with the same no-name scan.
/// </summary>
public sealed class ChildTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Version = "4.2.0-childtelemetry.7";

    private static readonly string[] AssignTags = ["gozleme.manifest.family", ChildTelemetry.DeclaredTag, ChildTelemetry.CreatedTag, ChildTelemetry.MatchedTag, ChildTelemetry.AbsentTag];

    private static readonly string[] Kinds = ["tool", "resource", "resource-template", "prompt", "skill", "operation", "webhook", "channel"];

    private static readonly string[] Families = ["mcp", "a2a", "api", "event", "skill", "model"];

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task Assignment_and_generations_are_observable_with_bounded_labels_and_no_names()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var capability = await OrgClient.GetJsonAsync(_s.Erin, OrgClient.Capability(_s.OrgA, _s.PublisherId, capabilityId));
        var path = _s.Revisions(capabilityId);
        var first = await RevisionClient.PublishedAsync(_s.Carol, path, ChildClient.Mcp(canonical, "1.0.0", ("tool", "zebra_lookup", "Zebra Lookup Title"), ("tool", "quokka_fetch", null), ("prompt", "wombat_brief", null)));
        var second = await RevisionClient.PublishedAsync(_s.Carol, path, ChildClient.Mcp(canonical, Version, ("tool", "zebra_lookup", "Zebra Lookup Title"), ("resource", "platypus_docs", "Platypus Docs Title")));
        var secondId = second.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ManifestClient.SendAsync(_s.Carol, path, ChildClient.A2aWithDuplicateSkill(canonical, "9.0.0"))).Status);

        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, secondId));
        var childId = ChildClient.Ids(children)[0];
        Assert.Equal(HttpStatusCode.OK, (await RevisionClient.GetAsync(_s.Erin, ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, childId))).Status);
        var unknown = Guid.CreateVersion7().ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await RevisionClient.GetAsync(_s.Erin, ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, unknown))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await RevisionClient.GetAsync(_s.Bob, ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, childId))).Status);
        var (_, generations) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, secondId));
        var generationId = ChildClient.Ids(generations, "id")[0];
        using (var download = await _s.Erin.GetAsync(new Uri($"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, secondId)}/{generationId}", UriKind.Relative), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        var spans = _s.Factory.SpansSnapshot();
        var publishes = spans.Where(s => s.DisplayName == RevisionTelemetry.PublishSpanName).ToDictionary(s => s.SpanId);
        var assigns = spans.Where(s => s.DisplayName == ChildTelemetry.AssignSpanName).ToList();
        Assert.Equal(2, assigns.Count(s => publishes.ContainsKey(s.ParentSpanId)));
        Assert.All(assigns, s => Assert.Equal(AssignTags.Order(StringComparer.Ordinal), s.TagObjects.Select(t => t.Key).Order(StringComparer.Ordinal)));
        var later = Assert.Single(assigns, s => Tag(s, ChildTelemetry.DeclaredTag) == "2" && publishes.ContainsKey(s.ParentSpanId) && Tag(publishes[s.ParentSpanId], "gozleme.revision.id") == secondId);
        Assert.Equal(("1", "1", "2"), (Tag(later, ChildTelemetry.CreatedTag), Tag(later, ChildTelemetry.MatchedTag), Tag(later, ChildTelemetry.AbsentTag)));

        var records = spans.Where(s => s.DisplayName == ChildTelemetry.GenerationSpanName && publishes.ContainsKey(s.ParentSpanId)).ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, s => Assert.Equal(("1", "True"), (Tag(s, ChildTelemetry.GenerationTag), Tag(s, ChildTelemetry.AttestedTag))));

        // T076: the spans enclose their work, so the database commands are their children.
        Assert.Contains(spans, s => s.ParentSpanId == later.SpanId);
        Assert.All(records, r => Assert.Contains(spans, s => s.ParentSpanId == r.SpanId));

        // Read spans carry the opaque IDs only once resolved: never for an unknown child or a non-member.
        Assert.Contains(spans, s => Tag(s, ChildTelemetry.ChildIdTag) == childId);
        Assert.Contains(spans, s => Tag(s, ChildTelemetry.GenerationIdTag) == generationId);
        Assert.DoesNotContain(spans, s => Tag(s, ChildTelemetry.ChildIdTag) == unknown);
        Assert.Single(spans, s => Tag(s, ChildTelemetry.ChildIdTag) == childId && Tag(s, "http.response.status_code") is null or "200");

        _s.Factory.FlushMetrics();
        Metric[] metrics;
        lock (_s.Factory.Metrics)
        {
            metrics = [.. _s.Factory.Metrics];
        }

        var assignments = Points(metrics, "gozleme.children.assignments");
        Assert.Contains("gozleme.child.kind=tool;gozleme.children.outcome=created;gozleme.manifest.family=mcp", assignments);
        Assert.Contains("gozleme.child.kind=tool;gozleme.children.outcome=matched;gozleme.manifest.family=mcp", assignments);
        Assert.Contains("gozleme.child.kind=resource;gozleme.children.outcome=created;gozleme.manifest.family=mcp", assignments);
        var recorded = Points(metrics, "gozleme.projection.generations");
        Assert.Contains("gozleme.manifest.family=mcp;gozleme.projection.outcome=recorded", recorded);
        string[] outcomes = [ChildOutcome.Created, ChildOutcome.Matched, ChildOutcome.Duplicate, ChildOutcome.Recorded, ChildOutcome.Identical, ChildOutcome.Refused];
        foreach (var label in assignments.Concat(recorded).SelectMany(p => p.Split(';')))
        {
            var (key, value) = (label.Split('=')[0], label.Split('=')[1]);
            Assert.Contains(value, key switch { "gozleme.manifest.family" => Families, ChildTelemetry.KindTag => Kinds, _ => outcomes });
        }

        var exported = string.Join('\n', spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
            .Concat(s.Events.Select(e => e.Name + string.Join(',', e.Tags.Select(t => $"{t.Key}={t.Value}"))))));
        var logs = string.Join('\n', _s.Factory.Logs);
        string[] forbidden =
        [
            "zebra_lookup", "quokka_fetch", "wombat_brief", "platypus_docs", "Zebra Lookup Title", "Platypus Docs Title", Version,
            first.GetProperty("projectionDigest").GetString()!["sha256:".Length..], second.GetProperty("projectionDigest").GetString()!["sha256:".Length..],
            capability.GetProperty("name").GetString()!, capability.GetProperty("alias").GetString()!,
        ];
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(assignments.Concat(recorded), p => p.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_install_span_carries_counts_only_and_projection_regenerate_carries_its_exit_code()
    {
        // T075: both run outside the API, so a listener collects them; other suites may run alongside, hence Contains.
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        await using var db = await GozlemeDatabase.StartAsync();
        await using var s = await RevisionScenario.CreateAsync(db);
        string[] names = ["numbat_install", "echidna_install", "Numbat Install Title"];
        var revisions = new List<string>();
        foreach (var name in names[..2])
        {
            var (capabilityId, canonical) = await s.NewCapabilityAsync();
            var revision = await RevisionClient.PublishedAsync(s.Carol, s.Revisions(capabilityId), ChildClient.Mcp(canonical, Version, ("tool", name, names[2])));
            revisions.Add(revision.GetProperty("id").GetString()!);
        }

        await GozlemeDatabase.ExecuteAsync(
            "SET session_replication_role = replica; DELETE FROM gozleme.revision_children; DELETE FROM gozleme.capability_children; DELETE FROM gozleme.projection_generations;",
            db.SuperuserConnectionString);
        Assert.Equal(0, await db.MigrateAsync());
        Assert.Equal(ProjectionOperatorCommands.Ok, (await s.RegenerateAsync(null, null, "--revision", revisions[0])).Code);
        Assert.Equal(ProjectionOperatorCommands.UnknownTarget, (await s.RegenerateAsync(null, null, "--revision", Guid.CreateVersion7().ToString())).Code);

        Activity[] seen;
        lock (spans)
        {
            seen = [.. spans];
        }

        var install = Assert.Single(seen, a => a.DisplayName == ChildTelemetry.InstallSpanName
            && Tag(a, ChildTelemetry.RevisionCountTag) == "2" && Tag(a, ChildTelemetry.CapabilityCountTag) == "2"
            && seen.Count(c => c.ParentSpanId == a.SpanId && c.DisplayName == ChildTelemetry.AssignSpanName) == 2);
        Assert.Equal([ChildTelemetry.CapabilityCountTag, ChildTelemetry.RevisionCountTag], install.TagObjects.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.All(seen.Where(c => c.ParentSpanId == install.SpanId && c.DisplayName == ChildTelemetry.AssignSpanName),
            a => Assert.Equal("0", Tag(a, ChildTelemetry.DuplicatesTag)));

        const string regenerate = "gozleme.operator.projection_regenerate";
        Assert.Contains(seen, a => a.DisplayName == regenerate && Tag(a, PublisherTelemetry.ExitCodeTag) == "0"
            && seen.Any(g => g.TraceId == a.TraceId && g.DisplayName == ChildTelemetry.GenerationSpanName && Tag(g, ChildTelemetry.GenerationTag) == "2" && Tag(g, ChildTelemetry.AttestedTag) == "False"));
        Assert.Contains(seen, a => a.DisplayName == regenerate && Tag(a, PublisherTelemetry.ExitCodeTag) == "2" && a.Status == ActivityStatusCode.Error);

        var exported = string.Join('\n', seen.SelectMany(a => a.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(a.DisplayName).Append(a.StatusDescription ?? string.Empty)));
        foreach (var value in names.Append(Version))
        {
            Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
        }
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Gozleme.Api.Telemetry.GozlemeTelemetry.ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
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
}
