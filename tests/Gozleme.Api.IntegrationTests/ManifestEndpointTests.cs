using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 through the API (contracts/manifest-validation.md): US1 every family's valid sample is accepted with three
/// separate parts and byte-identical repeats (FR-015); US2 identity mismatches without an existence oracle (FR-011)
/// and offline references (FR-006); US4 the role × outcome matrix and the exact order origin → membership →
/// permission → target → suspension → budget → media type → size → validation (SC-005).
/// </summary>
public sealed class ManifestEndpointTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string EmptyNotFound = "";

    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!; // A: admin, member
    private HttpClient _carol = null!; // A: member, publisher
    private HttpClient _erin = null!; // A: member
    private HttpClient _bob = null!; // B: admin, member
    private HttpClient _bobPublisher = null!; // B: member, publisher
    private string _orgA = null!;
    private string _orgB = null!;
    private string _publisherId = null!;
    private string _capabilityId = null!;
    private string _canonical = null!;
    private string _alias = null!;
    private string _foreignPublisherId = null!;
    private string _foreignCapabilityId = null!;
    private string _foreignCanonical = null!;

    private GozlemeDatabase Db => fixture.Database;

    private string Path => ManifestClient.Validate(_orgA, _publisherId, _capabilityId);

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "10000" });
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_erin, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = await OrgClient.CreateAsync(_alice, "Manifests A");
        _orgB = await OrgClient.CreateAsync(_bob, "Manifests B");
        (_carol, _) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(_orgA));
        (_bobPublisher, _) = await TwoTenants.AddPublisherMemberAsync(_factory, _bob, Guid.Parse(_orgB));
        await OrgClient.JoinAsync(_alice, _erin, _orgA);

        var handle = OrgClient.NewHandle("mf");
        _publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA, handle)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, _publisherId, "manifest-demo");
        (_capabilityId, _canonical, _alias) = (capability.GetProperty("id").GetString()!, capability.GetProperty("canonicalId").GetString()!, capability.GetProperty("alias").GetString()!);

        _foreignPublisherId = (await OrgClient.RegisterPublisherAsync(_bob, _orgB)).GetProperty("id").GetString()!;
        var foreign = await OrgClient.CreateCapabilityAsync(_bobPublisher, _orgB, _foreignPublisherId, "manifest-demo");
        (_foreignCapabilityId, _foreignCanonical) = (foreign.GetProperty("id").GetString()!, foreign.GetProperty("canonicalId").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _alice, _carol, _erin, _bob, _bobPublisher })
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    public static TheoryData<string> ValidSamples => [.. ManifestClient.Samples.Where(s => s.Valid).Select(s => s.Id)];

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public async Task Every_valid_sample_is_accepted_with_three_separate_parts(string id)
    {
        var sample = ManifestClient.SampleById(id);
        var (status, body, response) = await ManifestClient.PostAsync(_carol, Path, sample.Body(_canonical));
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        }

        var json = ManifestClient.Json(body);
        Assert.Equal("accepted", json.GetProperty("outcome").GetString());
        Assert.Equal(["outcome", "envelope", "native", "projection"], json.EnumerateObject().Select(p => p.Name));
        Assert.False(json.GetProperty("envelope").TryGetProperty("document", out _));
        Assert.False(json.GetProperty("envelope").GetProperty("native").TryGetProperty("document", out _));
        var native = json.GetProperty("native");
        Assert.Equal(sample.Document, native.GetProperty("document").GetString());
        var bytes = Encoding.UTF8.GetBytes(sample.Document);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), native.GetProperty("digest").GetString());
        Assert.Equal(bytes.Length, native.GetProperty("byteLength").GetInt32());
        Assert.Equal("1", json.GetProperty("projection").GetProperty("projectionVersion").GetString());
        Assert.Equal(sample.Family, json.GetProperty("projection").GetProperty("family").GetString());

        // FR-015: the same request gives the same bytes; the alias is an equal claim.
        Assert.Equal((HttpStatusCode.OK, body), await ManifestClient.SendAsync(_carol, Path, sample.Body(_canonical)));
        var byAlias = ManifestClient.Json((await ManifestClient.SendAsync(_carol, Path, sample.Body(_alias))).Body);
        Assert.Equal("accepted", byAlias.GetProperty("outcome").GetString());
        Assert.Equal(json.GetProperty("projection").GetRawText(), byAlias.GetProperty("projection").GetRawText());
    }

    [Fact]
    public async Task Mcp_mode_is_derived_from_the_declared_revision()
    {
        foreach (var (id, mode) in new[] { ("valid-stateless-2026-07-28", "stateless"), ("valid-stateful-2025-11-25", "stateful") })
        {
            var json = ManifestClient.Json((await ManifestClient.SendAsync(_carol, Path, ManifestClient.SampleById(id).Body(_canonical))).Body);
            var projection = json.GetProperty("projection");
            Assert.Equal(mode, projection.GetProperty("attributes").GetProperty("mode").GetString());
            Assert.Contains("/attributes/mode", projection.GetProperty("derived").EnumerateArray().Select(d => d.GetString()));
        }
    }

    [Fact]
    public async Task Yaml_and_json_openapi_project_the_same_shape()
    {
        foreach (var id in new[] { "valid-openapi-3.1", "valid-openapi-3.0" })
        {
            var projection = ManifestClient.Json((await ManifestClient.SendAsync(_carol, Path, ManifestClient.SampleById(id).Body(_canonical))).Body).GetProperty("projection");
            Assert.Equal("openapi-api", projection.GetProperty("kind").GetString());
            Assert.NotEmpty(projection.GetProperty("entries").EnumerateArray());
        }
    }

    [Fact]
    public async Task Identity_mismatches_are_one_answer_whatever_the_claim_names()
    {
        var document = ManifestClient.SampleById("valid-model-card").Document;
        var claims = new[]
        {
            _foreignCanonical,
            $"gz://{GozlemeDatabase.Origin}/cap/{Guid.CreateVersion7()}",
            _canonical.Replace(GozlemeDatabase.Origin, "registry.other.example.test", StringComparison.Ordinal),
            _canonical.ToUpperInvariant(),
            " " + _canonical,
            _canonical + "\n",
            _alias.ToUpperInvariant(),
            _alias.Split('/')[0] + "/other",
            _foreignCapabilityId,
        };

        var answers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            var (status, body) = await ManifestClient.SendAsync(_carol, Path, ManifestClient.Envelope(claim, "model", "gozleme-model-card", "1", "json", document));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            answers.Add(body);
        }

        var answer = ManifestClient.Json(Assert.Single(answers));
        var problem = Assert.Single(answer.GetProperty("problems").EnumerateArray());
        Assert.Equal(("identity_mismatch", "identity.capability"), (problem.GetProperty("category").GetString(), problem.GetProperty("rule").GetString()));
        Assert.Equal("""{"part":"envelope","pointer":"/capability"}""", problem.GetProperty("location").GetRawText());
    }

    [Fact]
    public async Task An_external_reference_is_rejected_without_being_fetched()
    {
        var sample = ManifestClient.SampleById("invalid-external-ref");
        var json = ManifestClient.Json((await ManifestClient.SendAsync(_carol, Path, sample.Body(_canonical))).Body);
        Assert.Equal("external_reference", json.GetProperty("problems")[0].GetProperty("category").GetString());
        Assert.DoesNotContain(_factory.Logs, l => l.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OrganizationRole.Publisher, HttpStatusCode.OK)]
    [InlineData(OrganizationRole.Admin, HttpStatusCode.Forbidden)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    [InlineData(OrganizationRole.Reviewer, HttpStatusCode.Forbidden)]
    [InlineData(OrganizationRole.Approver, HttpStatusCode.Forbidden)]
    public async Task Each_role_alone_gets_its_contract_answer(string role, HttpStatusCode expected)
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _c = client;
        await OrgClient.JoinAsync(_alice, client, _orgA);
        var userId = await OrgClient.UserIdAsync(client);
        await OrgClient.SetRolesAsync(_alice, _orgA, userId, role == OrganizationRole.Admin ? [OrganizationRole.Admin] : [role]);

        var valid = ManifestClient.SampleById("valid-model-card").Body(_canonical);
        var (status, body) = await ManifestClient.SendAsync(client, Path, valid);
        Assert.Equal(expected, status);
        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal("""{"error":"forbidden"}""", body);
            Assert.Equal((HttpStatusCode.Forbidden, body), await ManifestClient.SendAsync(client, Path, "{not json"u8.ToArray())); // never parsed
        }
    }

    [Fact]
    public async Task Unknown_malformed_foreign_and_misplaced_targets_get_the_same_empty_404()
    {
        var valid = ManifestClient.SampleById("valid-model-card").Body(_canonical);
        var otherPublisher = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        string[] paths =
        [
            ManifestClient.Validate(_orgA, _publisherId, Guid.CreateVersion7().ToString()),
            ManifestClient.Validate(_orgA, _publisherId, "not-a-uuid"),
            ManifestClient.Validate(_orgA, Guid.CreateVersion7().ToString(), _capabilityId),
            ManifestClient.Validate(_orgA, otherPublisher, _capabilityId),
            ManifestClient.Validate(_orgA, _foreignPublisherId, _foreignCapabilityId),
            ManifestClient.Validate(_orgB, _foreignPublisherId, _foreignCapabilityId), // carol is not a member of B
            ManifestClient.Validate(Guid.CreateVersion7().ToString(), _publisherId, _capabilityId),
            ManifestClient.Validate("nope", _publisherId, _capabilityId),
        ];
        foreach (var path in paths)
        {
            var (status, body, response) = await ManifestClient.PostAsync(_carol, path, valid);
            using (response)
            {
                Assert.Equal((HttpStatusCode.NotFound, EmptyNotFound), (status, body));
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }

        // A non-member of A never sees 403 for A's real capability.
        Assert.Equal((HttpStatusCode.NotFound, EmptyNotFound), await ManifestClient.SendAsync(_bob, Path, valid));
    }

    [Fact]
    public async Task Media_type_and_size_are_checked_after_permission_and_before_parsing()
    {
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, """{"error":"unsupported_media_type"}"""), await ManifestClient.SendAsync(_carol, Path, "{}"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(_erin, Path, "{}"u8.ToArray(), "text/plain")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ManifestClient.SendAsync(_carol, Path, "{}"u8.ToArray(), "Application/JSON; charset=utf-8")).Status);

        var huge = new byte[(1024 * 1024) + 1];
        Array.Fill(huge, (byte)' ');
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, """{"error":"too_large"}"""), await ManifestClient.SendAsync(_carol, Path, huge));
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(_erin, Path, huge)).Status);
    }

    [Fact]
    public async Task A_suspended_publisher_answers_409_without_reading_the_body()
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "suspended-demo");
        await GozlemeDatabase.ExecuteAsync(
            $"UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE publisher_id = '{publisherId}'", Db.MigratorConnectionString);
        var path = ManifestClient.Validate(_orgA, publisherId, capability.GetProperty("id").GetString()!);
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"suspended"}"""), await ManifestClient.SendAsync(_carol, path, "{not json"u8.ToArray()));
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"suspended"}"""), await ManifestClient.SendAsync(_carol, path, "{}"u8.ToArray(), "text/plain"));
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(_erin, path, "{}"u8.ToArray())).Status);
    }

    [Fact]
    public async Task The_budget_is_per_account_and_answers_429_with_retry_after()
    {
        await using var limited = new GozlemeApiFactory(Db.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "2" });
        var (alice, _) = await AuthClient.SignedInAsync(limited);
        var org = await OrgClient.CreateAsync(alice, "Budget");
        var (carol, _) = await TwoTenants.AddPublisherMemberAsync(limited, alice, Guid.Parse(org));
        var (dave, _) = await TwoTenants.AddPublisherMemberAsync(limited, alice, Guid.Parse(org));
        using var _a = alice;
        using var _c = carol;
        using var _d = dave;
        var publisherId = (await OrgClient.RegisterPublisherAsync(alice, org)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(carol, org, publisherId, "budget");
        var path = ManifestClient.Validate(org, publisherId, capability.GetProperty("id").GetString()!);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ManifestClient.SendAsync(carol, path, "{}"u8.ToArray())).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ManifestClient.SendAsync(carol, path, "{}"u8.ToArray())).Status);
        var (status, body, response) = await ManifestClient.PostAsync(carol, path, "{}"u8.ToArray(), "text/plain");
        using (response)
        {
            Assert.Equal((HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""), (status, body)); // before the media type
            Assert.True(int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 60);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ManifestClient.SendAsync(dave, path, "{}"u8.ToArray())).Status); // another account's budget
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(alice, path, "{}"u8.ToArray())).Status); // no permission, no permit
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(alice, path, "{}"u8.ToArray())).Status);
        }
    }

    [Fact]
    public async Task Rejected_results_never_echo_submitted_text()
    {
        var secrets = JsonSerializer.Deserialize<string[]>(File.ReadAllText(System.IO.Path.Combine(ManifestClient.SampleDirectory, "synthetic-secrets.json")))!;
        foreach (var sample in ManifestClient.Samples.Where(s => !s.Valid))
        {
            var (status, body) = await ManifestClient.SendAsync(_carol, Path, sample.Body(_canonical));
            Assert.Equal(sample.Status, status);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(JsonEncodedText.Encode(secret).ToString(), body, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }
        }
    }
}
