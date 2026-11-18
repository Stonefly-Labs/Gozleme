using System.Net;
using System.Text.RegularExpressions;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T060 (US4-AS3–AS5, FR-013): no rotation, replacement, transfer, delegation or generic publish route exists
/// for any method; API responses never use the banned trust wording; and a manifest that itself claims a signature is
/// refused <c>reserved_claim</c> exactly as validate refuses it.
/// </summary>
public sealed class RevisionBoundaryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    internal static readonly string[] BannedWording = ["delegat", "transfer", "chain of trust", "transparency", "publisher-held"];

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task Key_rotation_transfer_delegation_and_generic_publish_routes_do_not_exist()
    {
        var keys = RevisionClient.Keys(_s.OrgA, _s.PublisherId);
        string[] paths = [keys + "/rotate", keys + "/replace", keys + "/transfer", keys + "/delegate", "/api/v1/publish"];
        foreach (var path in paths)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
                if (method != HttpMethod.Get)
                {
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                }

                using var response = await _s.Carol.SendAsync(request, TestContext.Current.CancellationToken);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task Responses_never_use_the_banned_trust_wording()
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync();
        var revisionPath = _s.Revision(capabilityId, revision.GetProperty("id").GetString()!);
        var keys = RevisionClient.Keys(_s.OrgA, _s.PublisherId);
        var keyUuid = (await RevisionClient.GetAsync(_s.Erin, keys)).Body.GetProperty("keys")[0].GetProperty("id").GetString();
        foreach (var path in new[] { _s.Revisions(capabilityId), revisionPath, revisionPath + "/statement", keys, $"{keys}/{keyUuid}/public-key" })
        {
            var text = await _s.Erin.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            foreach (var banned in BannedWording)
            {
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Contains("It is not a signature made by the publisher.", revision.GetProperty("attestation").GetProperty("proof").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid-a2a-signatures")]
    [InlineData("invalid-reserved-envelope")]
    [InlineData("invalid-reserved-openapi")]
    public async Task A_manifest_claiming_a_signature_is_refused_exactly_as_validate(string sample)
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var body = ManifestClient.SampleById(sample).Body(canonical);
        var published = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.Status);
        Assert.Contains("\"reserved_claim\"", published.Body, StringComparison.Ordinal);
        Assert.Equal(await ManifestClient.SendAsync(_s.Carol, ManifestClient.Validate(_s.OrgA, _s.PublisherId, capabilityId), body), published);
        Assert.Equal(0, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
    }
}

/// <summary>
/// GOZ-0007 T062 (US4-AS1): the statement v1 contract defines no delegation, transfer, chain, witness or transparency
/// member; those words appear only in its "Unsupported in v1" statement.
/// </summary>
public sealed partial class RevisionContractWordingTests
{
    private static readonly string[] Unsupported = ["delegat", "transfer", "chain", "witness", "transparency"];

    [GeneratedRegex(@"^\|\s*`(?<member>[A-Za-z.]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex MemberRow();

    [Fact]
    public void The_statement_contract_defines_no_unsupported_member()
    {
        var contract = File.ReadAllText(GozlemeDatabase.RepoPath("docs/specs/007-immutable-revisions/contracts/attestation-statement-v1.md"));
        var members = MemberRow().Matches(contract).Select(m => m.Groups["member"].Value).ToList();
        Assert.Equal(["adapter", "capability", "key", "native", "projection", "publishedAt", "publisher", "registryOrigin", "sequence", "statementVersion", "type", "version"],
            members.Order(StringComparer.Ordinal));
        Assert.All(members, m => Assert.DoesNotContain(Unsupported, u => m.Contains(u, StringComparison.OrdinalIgnoreCase)));

        // Every paragraph that names an unsupported mechanism is the "Unsupported in v1" statement itself.
        var paragraphs = contract.Split("\n\n").Where(p => Unsupported.Any(u => p.Contains(u, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.NotEmpty(paragraphs);
        Assert.All(paragraphs, p => Assert.StartsWith("**Unsupported in v1**", p.TrimStart(), StringComparison.Ordinal));
    }
}
