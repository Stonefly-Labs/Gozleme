using System.Globalization;
using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T063 (SC-006, US5, contracts/permissions-delta.md): every role against publish, list, read, the four
/// downloads, the key list and the public key; admin without <c>publisher</c> is refused <c>403</c> before the body is
/// parsed; non-members and unknown, malformed, foreign or misplaced IDs get the same empty <c>404</c>; a suspended
/// publisher is <c>409</c> in the API and refused by the write policy in SQL; an unverified origin is <c>503</c>; role
/// loss applies to the next request; and the 11th publication in a minute is <c>429</c> with <c>Retry-After</c>.
/// </summary>
public sealed class RevisionAuthorizationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;
    private HttpClient _dave = null!; // signed in, member of no organization here

    public async ValueTask InitializeAsync()
    {
        _s = await RevisionScenario.CreateAsync(fixture.Database);
        (_dave, _) = await AuthClient.SignedInAsync(_s.Factory);
    }

    public async ValueTask DisposeAsync()
    {
        _dave.Dispose();
        await _s.DisposeAsync();
    }

    /// <summary>The eight read routes for one revision and its key.</summary>
    private static string[] ReadPaths(string org, string publisherId, string capabilityId, string revisionId, string keyUuid)
    {
        var revision = RevisionClient.Revision(org, publisherId, capabilityId, revisionId);
        return
        [
            RevisionClient.Revisions(org, publisherId, capabilityId), revision, revision + "/native", revision + "/projection", revision + "/statement",
            revision + "/signature", RevisionClient.Keys(org, publisherId), $"{RevisionClient.Keys(org, publisherId)}/{keyUuid}/public-key",
        ];
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> GetBytesAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private async Task<string> KeyUuidAsync(string publisherId) =>
        (await RevisionClient.GetAsync(_s.Erin, RevisionClient.Keys(_s.OrgA, publisherId))).Body.GetProperty("keys")[0].GetProperty("id").GetString()!;

    [Fact]
    public async Task Every_role_reads_and_only_publisher_publishes()
    {
        var (capabilityId, canonical, revision) = await _s.PublishedAsync();
        var paths = ReadPaths(_s.OrgA, _s.PublisherId, capabilityId, revision.GetProperty("id").GetString()!, await KeyUuidAsync(_s.PublisherId));
        foreach (var client in new[] { _s.Alice, _s.Carol, _s.Erin })
        {
            foreach (var path in paths)
            {
                Assert.True((await GetBytesAsync(client, path)).Status == HttpStatusCode.OK, path);
            }
        }

        var publish = _s.Revisions(capabilityId);
        foreach (var client in new[] { _s.Alice, _s.Erin })
        {
            // Admin and member alike: 403 before the body is read, whatever it holds.
            Assert.Equal((HttpStatusCode.Forbidden, """{"error":"forbidden"}"""), await ManifestClient.SendAsync(client, publish, "{not json"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(client, publish, "{}"u8.ToArray(), "text/plain")).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(client, publish, RevisionClient.Envelope(canonical, "5.0.0"))).Status);
        }

        Assert.Equal(1, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
        Assert.True(OrganizationPermissions.Allows([OrganizationRole.Admin, OrganizationRole.Publisher], OrgAction.ManifestPublish, OrgResource.Manifest));
    }

    [Fact]
    public async Task Non_members_and_wrong_ids_get_the_same_empty_404()
    {
        var (capabilityId, canonical, revision) = await _s.PublishedAsync();
        var revisionId = revision.GetProperty("id").GetString()!;
        var keyUuid = await KeyUuidAsync(_s.PublisherId);
        var (otherCapability, _, otherRevision) = await _s.PublishedAsync("2.0.0");
        var (otherPublisher, _) = await _s.NewPublisherAsync();
        var (_, _, foreignRevision) = await _s.PublishedAsync("3.0.0", null);
        var fresh = Guid.CreateVersion7().ToString();

        // A non-member of A: every route of A's real revision.
        foreach (var path in ReadPaths(_s.OrgA, _s.PublisherId, capabilityId, revisionId, keyUuid))
        {
            Assert.Equal((HttpStatusCode.NotFound, 0), Shape(await GetBytesAsync(_dave, path)));
            Assert.Equal((HttpStatusCode.NotFound, 0), Shape(await GetBytesAsync(_s.Bob, path)));
        }

        Assert.Equal((HttpStatusCode.NotFound, ""), await ManifestClient.SendAsync(_dave, _s.Revisions(capabilityId), RevisionClient.Envelope(canonical, "4.0.0")));

        // Members of A with unknown, malformed, foreign and misplaced IDs.
        string[][] wrong =
        [
            [fresh, _s.PublisherId, capabilityId, revisionId, keyUuid], ["nope", _s.PublisherId, capabilityId, revisionId, keyUuid],
            [_s.OrgA, fresh, capabilityId, revisionId, keyUuid], [_s.OrgA, "nope", capabilityId, revisionId, keyUuid],
            [_s.OrgA, otherPublisher, capabilityId, revisionId, keyUuid],
            [_s.OrgA, _s.ForeignPublisherId, _s.ForeignCapabilityId, revisionId, keyUuid], [_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId, revisionId, keyUuid],
        ];
        foreach (var ids in wrong)
        {
            // otherPublisher is a real publisher of A: only its capability-scoped routes and A's key are misplaced.
            foreach (var path in ReadPaths(ids[0], ids[1], ids[2], ids[3], ids[4]).Where(p => ids[1] != otherPublisher || !p.EndsWith("/signing-keys", StringComparison.Ordinal)))
            {
                Assert.True(Shape(await GetBytesAsync(_s.Carol, path)) == (HttpStatusCode.NotFound, 0), path);
            }
        }

        // The right capability with a revision that is unknown, malformed or another capability's.
        foreach (var id in new[] { fresh, "nope", otherRevision.GetProperty("id").GetString()!, foreignRevision.GetProperty("id").GetString()! })
        {
            foreach (var suffix in new[] { "", "/native", "/signature" })
            {
                Assert.Equal((HttpStatusCode.NotFound, 0), Shape(await GetBytesAsync(_s.Carol, _s.Revision(capabilityId, id) + suffix)));
            }
        }

        // A key of another publisher, an unknown key and a malformed one.
        var (keyPublisher, _) = await _s.NewPublisherAsync();
        await _s.PublishedAsync("1.0.0", keyPublisher);
        foreach (var key in new[] { await KeyUuidAsync(keyPublisher), fresh, "nope" })
        {
            Assert.Equal((HttpStatusCode.NotFound, 0), Shape(await GetBytesAsync(_s.Carol, $"{RevisionClient.Keys(_s.OrgA, _s.PublisherId)}/{key}/public-key")));
        }

        Assert.NotEqual(otherCapability, capabilityId);
    }

    private static (HttpStatusCode, int) Shape((HttpStatusCode Status, byte[] Body) r) => (r.Status, r.Body.Length);

    [Fact]
    public async Task A_suspended_publisher_is_refused_in_the_API_and_by_the_write_policy()
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, _) = await _s.PublishedAsync("1.0.0", publisherId);
        await _s.ReclaimAsync(handle);
        var path = _s.Revisions(capabilityId, publisherId);
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"suspended"}"""), await ManifestClient.SendAsync(_s.Carol, path, "{not json"u8.ToArray()));
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"suspended"}"""), await ManifestClient.SendAsync(_s.Carol, path, RevisionClient.Envelope(canonical, "2.0.0")));
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(_s.Erin, path, "{}"u8.ToArray())).Status);

        var insert = RevisionScenario.InsertRevisionSql(_s.OrgA, publisherId, capabilityId, _s.CarolId, sequence: 2, version: "2.0.0");
        Assert.Equal("42501", await RevisionScenario.FailsAsync(_s.Db.AppConnectionString, RevisionScenario.Context(_s.CarolId), insert));
    }

    [Fact]
    public async Task Losing_the_publisher_role_applies_to_the_next_request()
    {
        var (dana, danaId) = await TwoTenants.AddPublisherMemberAsync(_s.Factory, _s.Alice, Guid.Parse(_s.OrgA));
        using var _d = dana;
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var path = _s.Revisions(capabilityId);
        await RevisionClient.PublishedAsync(dana, path, RevisionClient.Envelope(canonical, "1.0.0"));
        await OrgClient.SetRolesAsync(_s.Alice, _s.OrgA, danaId.ToString(), OrganizationRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(dana, path, RevisionClient.Envelope(canonical, "2.0.0"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await RevisionClient.GetAsync(dana, path)).Status); // still a member: reads stay

        using (var removed = await OrgClient.RemoveAsync(_s.Alice, _s.OrgA, danaId.ToString()))
        {
            Assert.True(removed.IsSuccessStatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await RevisionClient.GetAsync(dana, path)).Status);
    }

    [Fact]
    public async Task The_eleventh_publication_in_a_minute_is_rate_limited_with_retry_after()
    {
        await using var limited = await RevisionScenario.CreateAsync(_s.Db, new Dictionary<string, string?> { ["Gozleme:Manifests:PublishPerMinute"] = "10" });
        var (capabilityId, canonical) = await limited.NewCapabilityAsync();
        var path = limited.Revisions(capabilityId);

        // The budget is per clock minute (R15): start with at least 15 s left, so all 11 requests share one window.
        var second = await _s.Db.ScalarAsync<double>("SELECT extract(second FROM now())::float8", _s.Db.SuperuserConnectionString);
        if (second > 45)
        {
            await Task.Delay(TimeSpan.FromSeconds(61 - second), TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 10; i++)
        {
            // Refusals spend the budget too: it is taken before the body is read.
            var body = i % 2 == 0 ? RevisionClient.Envelope(canonical, $"1.{i}.0") : "{not json"u8.ToArray();
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await ManifestClient.SendAsync(limited.Carol, path, body)).Status);
        }

        var (status, text, response) = await ManifestClient.PostAsync(limited.Carol, path, "{}"u8.ToArray(), "text/plain");
        using (response)
        {
            Assert.Equal((HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""), (status, text)); // before the media type
            Assert.True(int.Parse(response.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture) is > 0 and <= 60);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(limited.Erin, path, "{}"u8.ToArray())).Status); // no permission, no permit
    }

    [Fact]
    public async Task An_unverified_origin_answers_503_before_membership()
    {
        await using var db = await GozlemeDatabase.StartAsync();
        await GozlemeDatabase.ExecuteAsync(
            "ALTER TABLE gozleme.registry_origin DISABLE TRIGGER registry_origin_guard; DELETE FROM gozleme.registry_origin; ALTER TABLE gozleme.registry_origin ENABLE TRIGGER registry_origin_guard;",
            db.SuperuserConnectionString);
        await using var factory = new GozlemeApiFactory(db.AppConnectionString);
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7().ToString()).ToArray();
        foreach (var path in ReadPaths(ids[0], ids[1], ids[2], ids[3], ids[4]))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetBytesAsync(alice, path)).Status);
        }

        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}"""), await ManifestClient.SendAsync(alice, RevisionClient.Revisions(ids[0], ids[1], ids[2]), "{}"u8.ToArray()));
    }
}
