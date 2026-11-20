using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 SC-007 (T069): publishing a manifest whose native document is 512 KiB (the native bound) completes in under
/// 2 s, and reading it back with its verification in under 1 s, after warm-up. The measured times are written to
/// <c>evidence/revision-evidence.json</c> under <c>performance</c>.
/// </summary>
public sealed class RevisionPerformanceTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const int MaxNativeBytes = 512 * 1024;

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task A_512_KiB_manifest_publishes_in_under_two_seconds_and_verifies_in_under_one()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var path = _s.Revisions(capabilityId);
        var document = ManifestPerformanceTests.OpenApi(MaxNativeBytes);
        Assert.Equal(MaxNativeBytes, Encoding.UTF8.GetByteCount(document));

        // Warm-up: schemas compile once, the signing key is created by the first publication.
        await RevisionClient.PublishedAsync(_s.Carol, path, ManifestClient.Envelope(canonical, "api", "openapi", "3.1", "json", ManifestPerformanceTests.OpenApi(4096)));
        var (publish, verify) = (new List<double>(), new List<double>());
        for (var i = 0; i < 3; i++)
        {
            var body = ManifestClient.Envelope(canonical, "api", "openapi", "3.1", "json", document, e => e["version"] = $"2.0.{i}");
            var watch = Stopwatch.StartNew();
            var revision = await RevisionClient.PublishedAsync(_s.Carol, path, body);
            publish.Add(watch.Elapsed.TotalMilliseconds);

            watch.Restart();
            var (status, read) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, revision.GetProperty("id").GetString()!));
            verify.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("verified", read.GetProperty("verification").GetProperty("status").GetString());
            Assert.Equal(MaxNativeBytes, read.GetProperty("native").GetProperty("byteLength").GetInt32());
        }

        Assert.True(publish.Max() < 2000, $"slowest publication {publish.Max():F0} ms");
        Assert.True(verify.Max() < 1000, $"slowest verification {verify.Max():F0} ms");
        Record(publish, verify);
    }

    private static void Record(List<double> publish, List<double> verify)
    {
        SpecEvidence.Record("GOZ-0007", "docs/specs/007-immutable-revisions/evidence/revision-evidence.json", "performance", new JsonObject
        {
            ["criterion"] = "SC-007",
            ["nativeBytes"] = MaxNativeBytes,
            ["format"] = "openapi 3.1 json",
            ["runs"] = 3,
            ["publishMedianMs"] = Math.Round(publish.Order().ElementAt(1)),
            ["publishWorstMs"] = Math.Round(publish.Max()),
            ["publishLimitMs"] = 2000,
            ["verifyMedianMs"] = Math.Round(verify.Order().ElementAt(1)),
            ["verifyWorstMs"] = Math.Round(verify.Max()),
            ["verifyLimitMs"] = 1000,
            ["environment"] = "integration test host (WebApplicationFactory, Testcontainers PostgreSQL)",
        });
    }
}
