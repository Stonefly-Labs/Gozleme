using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 SC-006: a generated OpenAPI native document of 512 KiB (the native bound) validates end to end in under
/// 2 s after warm-up. The measured time is written to <c>evidence/manifest-evidence.json</c> under <c>performance</c>.
/// </summary>
public sealed class ManifestPerformanceTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const int MaxNativeBytes = 512 * 1024;

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "100" });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>An OpenAPI 3.1 document of exactly <paramref name="bytes"/> UTF-8 bytes: many small paths, padded by a description.</summary>
    internal static string OpenApi(int bytes)
    {
        var paths = new StringBuilder();
        for (var i = 0; paths.Length < bytes - 4096; i++)
        {
            paths.Append(i == 0 ? string.Empty : ",").Append($"\"/r{i}\":{{\"get\":{{\"operationId\":\"op{i}\",\"responses\":{{\"200\":{{\"description\":\"ok\"}}}}}}}}");
            if (i == 990)
            {
                break; // stay under the 1,000-entry bound
            }
        }

        var head = "{\"openapi\":\"3.1.1\",\"info\":{\"title\":\"Perf\",\"version\":\"1\",\"description\":\"";
        var tail = "\"},\"paths\":{" + paths + "}}";
        return head + new string('d', bytes - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail)) + tail;
    }

    [Fact]
    public async Task A_512_KiB_openapi_document_validates_in_under_two_seconds()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Perf");
        var (carol, _) = await TwoTenants.AddPublisherMemberAsync(_factory, alice, Guid.Parse(org));
        using var _c = carol;
        var publisherId = (await OrgClient.RegisterPublisherAsync(alice, org)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(carol, org, publisherId, "perf");
        var path = ManifestClient.Validate(org, publisherId, capability.GetProperty("id").GetString()!);
        var canonical = capability.GetProperty("canonicalId").GetString()!;

        var document = OpenApi(MaxNativeBytes);
        Assert.Equal(MaxNativeBytes, Encoding.UTF8.GetByteCount(document));
        var body = ManifestClient.Envelope(canonical, "api", "openapi", "3.1", "json", document);

        // Warm-up: the schemas compile once; the first request also warms the request pipeline.
        Assert.Equal(HttpStatusCode.OK, (await ManifestClient.SendAsync(carol, path, ManifestClient.SampleById("valid-openapi-3.1").Body(canonical))).Status);
        var timings = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var (status, text) = await ManifestClient.SendAsync(carol, path, body);
            watch.Stop();
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("accepted", JsonNode.Parse(text)!["outcome"]!.GetValue<string>());
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var worst = timings.Max();
        Assert.True(worst < 2000, $"slowest {worst:F0} ms");
        Record(worst, timings.Order().ElementAt(1));
    }

    private static void Record(double worstMs, double medianMs)
    {
        SpecEvidence.Record("GOZ-0006", "docs/specs/006-typed-manifests/evidence/manifest-evidence.json", "performance", new JsonObject
        {
            ["criterion"] = "SC-006",
            ["nativeBytes"] = MaxNativeBytes,
            ["format"] = "openapi 3.1 json",
            ["runs"] = 3,
            ["medianMs"] = Math.Round(medianMs),
            ["worstMs"] = Math.Round(worstMs),
            ["limitMs"] = 2000,
            ["environment"] = "integration test host (WebApplicationFactory, Testcontainers PostgreSQL)",
        });
    }
}
