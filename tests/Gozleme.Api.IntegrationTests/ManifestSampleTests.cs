using System.Net;
using System.Text.Json.Nodes;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 research R15 (FR-018): every bundled sample, sent through the endpoint for its target, gives exactly the
/// <c>expected</c> its <c>index.json</c> entry declares: the outcome and, for a rejection, the one problem's category,
/// rule and location.
/// </summary>
public sealed class ManifestSampleTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _carol = null!;
    private string _path = null!;
    private string _canonical = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "10000" });
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        var org = await OrgClient.CreateAsync(_alice, "Samples");
        (_carol, _) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(org));
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, org)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(_carol, org, publisherId, "manifest-demo");
        _canonical = capability.GetProperty("canonicalId").GetString()!;
        _path = ManifestClient.Validate(org, publisherId, capability.GetProperty("id").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _carol.Dispose();
        await _factory.DisposeAsync();
    }

    public static TheoryData<string> Samples => [.. ManifestClient.Samples.Select(s => s.Id)];

    [Fact]
    public void The_samples_cover_every_family_and_every_category()
    {
        Assert.Equal(["a2a", "api", "event", "mcp", "model", "skill"], ManifestClient.Samples.Select(s => s.Family).Distinct().Order(StringComparer.Ordinal));
        Assert.All(ManifestClient.Samples.GroupBy(s => s.Family), g => Assert.Contains(g, s => !s.Valid));
        string[] categories = ["malformed", "too_large", "unsupported_version", "unsupported_family", "unsupported_format", "unknown_field", "reserved_claim", "secret_value", "identity_mismatch", "external_reference", "invalid_value"];
        Assert.Equal(categories.Order(StringComparer.Ordinal), ManifestClient.Samples.Where(s => !s.Valid).Select(s => s.Expected["category"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Each_sample_gives_exactly_its_expected_result(string id)
    {
        var sample = ManifestClient.SampleById(id);
        var (status, body) = await ManifestClient.SendAsync(_carol, _path, sample.Body(_canonical));
        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(sample.Expected["outcome"]!.GetValue<string>(), json["outcome"]!.GetValue<string>());
        if (sample.Valid)
        {
            return;
        }

        var problem = Assert.Single(json["problems"]!.AsArray())!;
        Assert.Equal(sample.Expected["category"]!.GetValue<string>(), problem["category"]!.GetValue<string>());
        Assert.Equal(sample.Expected["rule"]!.GetValue<string>(), problem["rule"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(sample.Expected["location"], problem["location"]), $"location {problem["location"]?.ToJsonString() ?? "null"}");
        Assert.False(json["truncated"]!.GetValue<bool>());
    }
}
