using System.Net;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US3 (FR-014–FR-017, SC-004): member list, removal on the next request, leaving, the last-member guard.</summary>
public sealed class MembershipTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Member_list_shows_only_id_email_join_date_and_how_they_joined()
    {
        var (alice, aliceEmail) = await AuthClient.SignedInAsync(_factory);
        var (bob, bobEmail) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        var org = await OrgClient.CreateAsync(alice, "Roster");
        await OrgClient.JoinAsync(alice, bob, org);

        var members = await OrgClient.MembersAsync(alice, org);
        Assert.Equal([aliceEmail, bobEmail], members.Select(m => m.GetProperty("email").GetString()));
        Assert.All(members, m => Assert.Equal(
            ["userId", "email", "joinedAt", "joinedVia", "isSelf"],
            m.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task Removal_takes_effect_on_the_next_request_without_signing_the_member_out()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        var shared = await OrgClient.CreateAsync(alice, "Shared");
        var bobsOwn = await OrgClient.CreateAsync(bob, "Bob's own");
        await OrgClient.JoinAsync(alice, bob, shared);
        var bobId = await OrgClient.UserIdAsync(bob);
        var stampBefore = await Db.ScalarAsync<string>($"SELECT security_stamp FROM gozleme.users WHERE id = '{bobId}'");

        using (var ok = await AuthClient.GetAsync(bob, $"{OrgClient.Base}/{shared}"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var removed = await OrgClient.RemoveAsync(alice, shared, bobId))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var gone = await AuthClient.GetAsync(bob, $"{OrgClient.Base}/{shared}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.Equal([bobsOwn], await OrgClient.IdsAsync(bob));
        using (var session = await AuthClient.GetAsync(bob, "/api/v1/auth/session"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        using (var own = await AuthClient.GetAsync(bob, $"{OrgClient.Base}/{bobsOwn}"))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        Assert.Equal(stampBefore, await Db.ScalarAsync<string>($"SELECT security_stamp FROM gozleme.users WHERE id = '{bobId}'"));
        await OrgClient.JoinAsync(alice, bob, shared); // a removed member can rejoin with a new code
        Assert.Contains(shared, await OrgClient.IdsAsync(bob));
    }

    [Fact]
    public async Task Leaving_moves_the_context_and_the_last_member_can_neither_leave_nor_be_removed()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        var solo = await OrgClient.CreateAsync(alice, "Solo");
        var aliceId = await OrgClient.UserIdAsync(alice);

        using (var last = await OrgClient.RemoveAsync(alice, solo, aliceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
            Assert.Equal("""{"error":"last_member"}""", await last.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Single(await OrgClient.MembersAsync(alice, solo));

        var pair = await OrgClient.CreateAsync(alice, "Pair");
        await OrgClient.JoinAsync(alice, bob, pair);
        Assert.Equal(pair, await OrgClient.ActiveAsync(alice));
        using (var leave = await OrgClient.RemoveAsync(alice, pair, aliceId))
        {
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        }

        Assert.Equal(solo, await OrgClient.ActiveAsync(alice));
        using var bobLast = await OrgClient.RemoveAsync(bob, pair, await OrgClient.UserIdAsync(bob));
        Assert.Equal(HttpStatusCode.Conflict, bobLast.StatusCode);
    }

    [Fact]
    public async Task Removing_or_leaving_revokes_the_departing_accounts_open_invitations()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var _c = carol;
        var org = await OrgClient.CreateAsync(alice, "Departures");
        await OrgClient.JoinAsync(alice, bob, org);
        await OrgClient.JoinAsync(alice, carol, org);
        var (bobsInvite, bobsCode) = await OrgClient.InviteAsync(bob, org);
        var (alicesInvite, _) = await OrgClient.InviteAsync(alice, org);
        var (carolsInvite, carolsCode) = await OrgClient.InviteAsync(carol, org);

        using (var removed = await OrgClient.RemoveAsync(alice, org, await OrgClient.UserIdAsync(bob)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        // Bob cannot rejoin through the code he created while a member; other members' codes stay open.
        using (var rejoin = await OrgClient.RedeemAsync(bob, bobsCode))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejoin.StatusCode);
        }

        Assert.Equal(new[] { alicesInvite, carolsInvite }.Order(), (await OpenInvitationIdsAsync(alice, org)).Order());
        Assert.Contains(_factory.Logs, l => l.Contains($"Invitation revoked {bobsInvite}", StringComparison.Ordinal));

        using (var left = await OrgClient.RemoveAsync(carol, org, await OrgClient.UserIdAsync(carol)))
        {
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }

        Assert.Equal([alicesInvite], await OpenInvitationIdsAsync(alice, org));
        using var carolRejoin = await OrgClient.RedeemAsync(carol, carolsCode);
        Assert.Equal(HttpStatusCode.BadRequest, carolRejoin.StatusCode);
    }

    private static async Task<string[]> OpenInvitationIdsAsync(HttpClient client, string orgId)
    {
        using var res = await AuthClient.GetAsync(client, $"{OrgClient.Base}/{orgId}/invitations");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. doc.RootElement.GetProperty("invitations").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task Concurrent_mutual_removal_always_leaves_a_member()
    {
        for (var round = 0; round < 5; round++)
        {
            var (alice, _) = await AuthClient.SignedInAsync(_factory);
            var (bob, _) = await AuthClient.SignedInAsync(_factory);
            using var _a = alice;
            using var _b = bob;
            var org = await OrgClient.CreateAsync(alice, "Standoff");
            await OrgClient.JoinAsync(alice, bob, org);
            var aliceId = await OrgClient.UserIdAsync(alice);
            var bobId = await OrgClient.UserIdAsync(bob);

            var results = await Task.WhenAll(
                Status(OrgClient.RemoveAsync(alice, org, bobId)),
                Status(OrgClient.RemoveAsync(bob, org, aliceId)));
            Assert.Single(results, s => s == HttpStatusCode.NoContent);
            Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{org}'"));
        }
    }

    [Fact]
    public async Task Removing_an_unknown_malformed_or_foreign_account_gets_the_same_404()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (outsider, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _o = outsider;
        var org = await OrgClient.CreateAsync(alice, "Targets");
        await OrgClient.CreateAsync(outsider, "Elsewhere");
        var outsiderId = await OrgClient.UserIdAsync(outsider);

        var results = new[]
        {
            await OrgClient.SnapshotAsync(alice, HttpMethod.Delete, $"{OrgClient.Base}/{org}/members/{outsiderId}"),
            await OrgClient.SnapshotAsync(alice, HttpMethod.Delete, $"{OrgClient.Base}/{org}/members/{Guid.NewGuid()}"),
            await OrgClient.SnapshotAsync(alice, HttpMethod.Delete, $"{OrgClient.Base}/{org}/members/not-a-uuid"),
        };
        Assert.All(results, r => Assert.Equal((HttpStatusCode.NotFound, string.Empty, true), r));
    }

    private static async Task<HttpStatusCode> Status(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        return response.StatusCode;
    }
}
