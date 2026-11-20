using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T052 (FR-012, FR-013, SC-006, contracts/permissions-delta.md): every role reads the five child and
/// projection routes; non-members and unknown, malformed, foreign or misplaced IDs get the same empty <c>404</c>; a
/// suspended publisher's children stay readable; an unverified origin is <c>503</c> before membership. T072: approver
/// and reviewer read them too, a member removed between requests gets the empty <c>404</c>, and a signed-out request
/// is <c>401</c>.
/// </summary>
public sealed class ChildAuthorizationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
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

    private static string[] Paths(string org, string publisherId, string capabilityId, string revisionId, string childId, string generationId) =>
    [
        ChildClient.Children(org, publisherId, capabilityId), ChildClient.Child(org, publisherId, capabilityId, childId),
        ChildClient.RevisionChildren(org, publisherId, capabilityId, revisionId), ChildClient.Projections(org, publisherId, capabilityId, revisionId),
        $"{ChildClient.Projections(org, publisherId, capabilityId, revisionId)}/{generationId}",
    ];

    private static async Task<(HttpStatusCode Status, int Length)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    private async Task<(string CapabilityId, string RevisionId, string ChildId, string GenerationId)> PublishedAsync(string? publisherId = null)
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync(publisherId: publisherId);
        var revisionId = revision.GetProperty("id").GetString()!;
        var publisher = publisherId ?? _s.PublisherId;
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, publisher, capabilityId, revisionId));
        var (_, generations) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Projections(_s.OrgA, publisher, capabilityId, revisionId));
        return (capabilityId, revisionId, ChildClient.Ids(children)[0], ChildClient.Ids(generations, "id")[0]);
    }

    [Fact]
    public async Task Every_role_reads_every_child_route()
    {
        var (capabilityId, revisionId, childId, generationId) = await PublishedAsync();
        foreach (var client in new[] { _s.Alice, _s.Carol, _s.Erin })
        {
            foreach (var path in Paths(_s.OrgA, _s.PublisherId, capabilityId, revisionId, childId, generationId))
            {
                Assert.True((await GetAsync(client, path)).Status == HttpStatusCode.OK, path);
            }
        }
    }

    [Fact]
    public async Task Approver_and_reviewer_read_every_child_route_and_lose_it_when_removed()
    {
        // T072 (US4/AC3, FR-015): the remaining roles, then a member removed between two requests.
        var (capabilityId, revisionId, childId, generationId) = await PublishedAsync();
        var paths = Paths(_s.OrgA, _s.PublisherId, capabilityId, revisionId, childId, generationId);
        foreach (var role in new[] { OrganizationRole.Approver, OrganizationRole.Reviewer })
        {
            var (client, id) = await TwoTenants.AddPublisherMemberAsync(_s.Factory, _s.Alice, Guid.Parse(_s.OrgA));
            using var _c = client;
            await OrgClient.SetRolesAsync(_s.Alice, _s.OrgA, id.ToString(), OrganizationRole.Member, role);
            foreach (var path in paths)
            {
                Assert.True((await GetAsync(client, path)).Status == HttpStatusCode.OK, $"{role} {path}");
            }

            using (var removed = await OrgClient.RemoveAsync(_s.Alice, _s.OrgA, id.ToString()))
            {
                Assert.True(removed.IsSuccessStatusCode);
            }

            foreach (var path in paths)
            {
                Assert.True(await GetAsync(client, path) == (HttpStatusCode.NotFound, 0), $"removed {role} {path}");
            }
        }
    }

    [Fact]
    public async Task A_signed_out_request_is_401_on_every_child_route()
    {
        var (capabilityId, revisionId, childId, generationId) = await PublishedAsync();
        using var anonymous = _s.Factory.CreateClient();
        foreach (var path in Paths(_s.OrgA, _s.PublisherId, capabilityId, revisionId, childId, generationId))
        {
            Assert.True((await GetAsync(anonymous, path)).Status == HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task Non_members_and_wrong_ids_get_the_same_empty_404()
    {
        var (capabilityId, revisionId, childId, generationId) = await PublishedAsync();
        var (otherCapability, otherRevision, otherChild, otherGeneration) = await PublishedAsync();
        var (otherPublisher, _) = await _s.NewPublisherAsync();
        var fresh = Guid.CreateVersion7().ToString();

        foreach (var path in Paths(_s.OrgA, _s.PublisherId, capabilityId, revisionId, childId, generationId))
        {
            Assert.Equal((HttpStatusCode.NotFound, 0), await GetAsync(_dave, path));
            Assert.Equal((HttpStatusCode.NotFound, 0), await GetAsync(_s.Bob, path));
        }

        string[][] wrong =
        [
            [fresh, _s.PublisherId, capabilityId], ["nope", _s.PublisherId, capabilityId], [_s.OrgA, fresh, capabilityId], [_s.OrgA, otherPublisher, capabilityId],
            [_s.OrgA, _s.PublisherId, fresh], [_s.OrgA, _s.PublisherId, "nope"], [_s.OrgA, _s.ForeignPublisherId, _s.ForeignCapabilityId], [_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId],
        ];
        foreach (var ids in wrong)
        {
            foreach (var path in Paths(ids[0], ids[1], ids[2], revisionId, childId, generationId))
            {
                Assert.True(await GetAsync(_s.Carol, path) == (HttpStatusCode.NotFound, 0), path);
            }
        }

        // Another capability's revision, child and generation, an unknown one and a malformed one, under the right capability.
        foreach (var (revision, child, generation) in new[] { (otherRevision, otherChild, otherGeneration), (fresh, fresh, fresh), ("nope", "nope", "nope") })
        {
            foreach (var path in Paths(_s.OrgA, _s.PublisherId, capabilityId, revision, child, generation).Skip(1))
            {
                Assert.True(await GetAsync(_s.Carol, path) == (HttpStatusCode.NotFound, 0), path);
            }
        }

        // The right revision with another revision's generation.
        Assert.Equal((HttpStatusCode.NotFound, 0), await GetAsync(_s.Carol, $"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)}/{otherGeneration}"));
        Assert.NotEqual(otherCapability, capabilityId);
    }

    [Fact]
    public async Task A_suspended_publishers_children_stay_readable()
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, revisionId, childId, generationId) = await PublishedAsync(publisherId);
        await _s.ReclaimAsync(handle);
        foreach (var path in Paths(_s.OrgA, publisherId, capabilityId, revisionId, childId, generationId))
        {
            Assert.True((await GetAsync(_s.Erin, path)).Status == HttpStatusCode.OK, path);
        }
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
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7().ToString()).ToArray();
        foreach (var path in Paths(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(alice, path)).Status);
        }
    }
}
