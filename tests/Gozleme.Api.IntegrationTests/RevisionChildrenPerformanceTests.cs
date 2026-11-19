using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Manifests;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 SC-006 (T061, research R12): publishing an MCP manifest with 1,000 entries (the GOZ-0006 bound), the first
/// time (1,000 new children) and again (1,000 matches), completes within the GOZ-0007 publication budget (2 s) plus
/// 1 s, after warm-up. The measured times are written to <c>evidence/children-evidence.json</c> under
/// <c>performance</c>.
/// </summary>
public sealed class RevisionChildrenPerformanceTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const int LimitMs = 2000 + 1000;

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task A_1000_entry_manifest_publishes_within_the_budget_plus_one_second()
    {
        var entries = Enumerable.Range(0, ManifestValidator.MaxEntries).Select(i => ("tool", $"tool_{i:D4}", (string?)$"Tool {i}")).ToArray();

        // Warm-up: schemas compile once, the signing key is created by the first publication.
        var (warm, warmCanonical) = await _s.NewCapabilityAsync();
        await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(warm), ChildClient.Mcp(warmCanonical, "0.1.0", entries));

        var (create, match) = (new List<double>(), new List<double>());
        for (var run = 0; run < 3; run++)
        {
            var (capabilityId, canonical) = await _s.NewCapabilityAsync();
            foreach (var (version, times) in new[] { ("1.0.0", create), ("1.1.0", match) })
            {
                var watch = Stopwatch.StartNew();
                var revision = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), ChildClient.Mcp(canonical, version, entries));
                times.Add(watch.Elapsed.TotalMilliseconds);
                var id = revision.GetProperty("id").GetString()!;
                Assert.Equal(ManifestValidator.MaxEntries, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.revision_children WHERE revision_id = '{id}'"));
            }

            Assert.Equal(ManifestValidator.MaxEntries, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.capability_children WHERE capability_id = '{capabilityId}'"));
        }

        var (status, _) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Children(_s.OrgA, _s.PublisherId, warm) + "?limit=100");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(create.Max() < LimitMs, $"slowest first publication {create.Max():F0} ms");
        Assert.True(match.Max() < LimitMs, $"slowest matching publication {match.Max():F0} ms");
        Record(create, match);
    }

    private static void Record(List<double> create, List<double> match)
    {
        var file = GozlemeDatabase.RepoPath("docs/specs/008-child-identities/evidence/children-evidence.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var evidence = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject { ["spec"] = "GOZ-0008" };
        evidence["performance"] = new JsonObject
        {
            ["criterion"] = "SC-006",
            ["entries"] = ManifestValidator.MaxEntries,
            ["format"] = "mcp-server-manifest 2026-07-28 json",
            ["runs"] = 3,
            ["createMedianMs"] = Math.Round(create.Order().ElementAt(1)),
            ["createWorstMs"] = Math.Round(create.Max()),
            ["matchMedianMs"] = Math.Round(match.Order().ElementAt(1)),
            ["matchWorstMs"] = Math.Round(match.Max()),
            ["limitMs"] = LimitMs,
            ["environment"] = "integration test host (WebApplicationFactory, Testcontainers PostgreSQL)",
        };
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(file, evidence.ToJsonString(options) + "\n");
    }
}
