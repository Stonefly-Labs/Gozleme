using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0004 US1 and US3 (FR-001–FR-013a, SC-001–SC-003): a role grant turns a denied operation into a
/// permitted one on the next request; every action matches the published role table; demotion revokes the
/// account's open invitations; an organization never loses its last admin.
/// </summary>
public sealed class RoleAssignmentTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Granting_admin_permits_a_rename_on_the_next_request_and_revoking_it_denies_again()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        var org = await OrgClient.CreateAsync(alice, "Grant");
        await OrgClient.JoinAsync(alice, carol, org);
        var carolId = await OrgClient.UserIdAsync(carol);
        var aliceId = await OrgClient.UserIdAsync(alice);

        using (var denied = await OrgClient.RenameAsync(carol, org, "Denied"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("""{"error":"forbidden"}""", await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal("Grant", (await Detail(carol, org)).GetProperty("displayName").GetString());
        Assert.Contains(_factory.Logs, l => l.Contains($"Authorization denied (organization.rename) in {org} {carolId}", StringComparison.Ordinal));

        using (var granted = await OrgClient.SetRolesRawAsync(alice, org, carolId, "admin"))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
            Assert.Equal(["admin", "member"], Roles(await AuthClient.JsonAsync(granted)));
        }

        Assert.Contains(_factory.Logs, l => l.Contains($"Member {carolId} roles changed from member to admin,member in {org} by {aliceId}", StringComparison.Ordinal));
        var detail = await Detail(carol, org);
        Assert.Equal(["admin", "member"], Roles(detail));
        Assert.Contains(OrgAction.OrganizationRename, detail.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        using (var permitted = await OrgClient.RenameAsync(carol, org, "Permitted"))
        {
            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        }

        await OrgClient.SetRolesAsync(alice, org, carolId);
        using (var again = await OrgClient.RenameAsync(carol, org, "Denied again"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        }

        using var session = await AuthClient.GetAsync(carol, "/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode); // still signed in; only the permission changed
    }

    [Fact]
    public async Task Role_changes_validate_normalize_and_hide_what_the_caller_cannot_see()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        using var _b = bob;
        var org = await OrgClient.CreateAsync(alice, "Validate");
        await OrgClient.JoinAsync(alice, carol, org);
        var other = await OrgClient.CreateAsync(bob, "Elsewhere");
        var (carolId, bobId, aliceId) = (await OrgClient.UserIdAsync(carol), await OrgClient.UserIdAsync(bob), await OrgClient.UserIdAsync(alice));

        // Non-member caller, and unknown, malformed or other-organization targets: identical empty 404.
        foreach (var (client, orgId, target) in new[] { (bob, org, carolId), (alice, org, Guid.NewGuid().ToString()), (alice, org, "not-a-guid"), (alice, org, bobId), (alice, other, carolId) })
        {
            using var hidden = await OrgClient.SetRolesRawAsync(client, orgId, target, "admin");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Empty(await hidden.Content.ReadAsStringAsync(ct));
        }

        using (var unknown = await OrgClient.SetRolesRawAsync(alice, org, carolId, "owner"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            var json = await AuthClient.JsonAsync(unknown);
            Assert.Equal("validation_failed", json.GetProperty("error").GetString());
            Assert.True(json.GetProperty("fields").TryGetProperty("roles", out _));
        }

        using (var missingMember = await OrgClient.SetRolesRawAsync(alice, org, carolId, "reviewer", "publisher"))
        {
            Assert.Equal(["member", "publisher", "reviewer"], Roles(await AuthClient.JsonAsync(missingMember)));
        }

        var changes = Changes(carolId);
        using (var same = await OrgClient.SetRolesRawAsync(alice, org, carolId, "publisher", "member", "reviewer"))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        }

        Assert.Equal(changes, Changes(carolId)); // the same set writes no second 1302

        using var self = await OrgClient.SetRolesRawAsync(carol, org, carolId, "admin");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode); // a member cannot raise its own roles
        Assert.Equal(["admin", "member"], Roles((await OrgClient.MembersAsync(alice, org)).Single(m => m.GetProperty("userId").GetString() == aliceId)));
    }

    [Fact]
    public async Task Losing_admin_revokes_the_accounts_open_invitations()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        var (dave, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        using var _d = dave;
        var org = await OrgClient.CreateAsync(alice, "Demotion");
        await OrgClient.JoinAsync(alice, carol, org);
        var carolId = await OrgClient.UserIdAsync(carol);
        await OrgClient.SetRolesAsync(alice, org, carolId, "admin");
        var (carolsInvite, carolsCode) = await OrgClient.InviteAsync(carol, org);
        var (alicesInvite, _) = await OrgClient.InviteAsync(alice, org);

        await OrgClient.SetRolesAsync(alice, org, carolId, "reviewer");

        Assert.Contains(_factory.Logs, l => l.Contains($"Invitation revoked {carolsInvite}", StringComparison.Ordinal));
        using (var redeem = await OrgClient.RedeemAsync(dave, carolsCode))
        {
            Assert.Equal(HttpStatusCode.BadRequest, redeem.StatusCode);
            Assert.Equal("invalid_invitation", (await AuthClient.JsonAsync(redeem)).GetProperty("error").GetString());
        }

        // Research R5 invariant: no non-admin holds an open invitation.
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"""
            SELECT count(*) FROM gozleme.invitations i JOIN gozleme.memberships m ON m.organization_id = i.organization_id AND m.user_id = i.created_by
            WHERE i.organization_id = '{org}' AND i.used_at IS NULL AND i.revoked_at IS NULL AND NOT 'admin' = ANY (m.roles)
            """));
        using var list = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}/invitations");
        Assert.Equal([alicesInvite], (await AuthClient.JsonAsync(list)).GetProperty("invitations").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("publisher")]
    [InlineData("reviewer")]
    [InlineData("approver")]
    [InlineData("admin")]
    public async Task Every_action_endpoint_matches_the_role_table(string role)
    {
        string[] granted = role.Length == 0 ? [] : [role];
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        using var _b = bob;
        var org = await OrgClient.CreateAsync(alice, "Matrix");
        await OrgClient.JoinAsync(alice, carol, org);
        await OrgClient.JoinAsync(alice, bob, org);
        var (carolId, bobId) = (await OrgClient.UserIdAsync(carol), await OrgClient.UserIdAsync(bob));
        await OrgClient.SetRolesAsync(alice, org, carolId, granted);
        var (invitationId, _) = await OrgClient.InviteAsync(alice, org);
        var permitted = OrganizationPermissions.For(OrganizationRole.Normalize(granted)!);

        // Destructive actions last: removing bob, then leaving.
        var calls = new (string Action, Func<Task<HttpResponseMessage>> Call)[]
        {
            (OrgAction.OrganizationRead, () => AuthClient.GetAsync(carol, $"{OrgClient.Base}/{org}")),
            (OrgAction.OrganizationSelect, () => OrgClient.SelectAsync(carol, org)),
            (OrgAction.OrganizationRename, () => OrgClient.RenameAsync(carol, org, "Matrix")),
            (OrgAction.MembershipList, () => AuthClient.GetAsync(carol, $"{OrgClient.Base}/{org}/members")),
            (OrgAction.MembershipRolesChange, () => OrgClient.SetRolesRawAsync(carol, org, bobId)),
            (OrgAction.InvitationList, () => AuthClient.GetAsync(carol, $"{OrgClient.Base}/{org}/invitations")),
            (OrgAction.InvitationCreate, () => OrgClient.InviteRawAsync(carol, org)),
            (OrgAction.InvitationRevoke, () => OrgClient.RevokeAsync(carol, org, invitationId)),
            (OrgAction.MembershipRemove, () => OrgClient.RemoveAsync(carol, org, bobId)),
            (OrgAction.MembershipLeave, () => OrgClient.RemoveAsync(carol, org, carolId)),
        };
        Assert.Equal(OrgAction.All.Select(a => a.Action).Order(), calls.Select(c => c.Action).Order());

        foreach (var (action, call) in calls)
        {
            using var response = await call();
            var expected = permitted.Contains(action);
            Assert.True(
                expected ? response.IsSuccessStatusCode : response.StatusCode == HttpStatusCode.Forbidden,
                $"{string.Join(',', granted)} {action}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task The_last_admin_can_neither_demote_itself_nor_leave()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        var solo = await OrgClient.CreateAsync(alice, "Alone");
        var aliceId = await OrgClient.UserIdAsync(alice);
        using (var last = await OrgClient.RemoveAsync(alice, solo, aliceId))
        {
            Assert.Equal("""{"error":"last_member"}""", await last.Content.ReadAsStringAsync(ct)); // unchanged
        }

        var org = await OrgClient.CreateAsync(alice, "Sole admin");
        await OrgClient.JoinAsync(alice, carol, org);
        var carolId = await OrgClient.UserIdAsync(carol);
        foreach (var attempt in new[] { OrgClient.SetRolesRawAsync(alice, org, aliceId), OrgClient.RemoveAsync(alice, org, aliceId) })
        {
            using var refused = await attempt;
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("""{"error":"last_admin"}""", await refused.Content.ReadAsStringAsync(ct));
        }

        await OrgClient.SetRolesAsync(alice, org, carolId, "admin");
        await OrgClient.SetRolesAsync(alice, org, aliceId); // another admin exists
        using var renamed = await OrgClient.RenameAsync(alice, org, "Next request");
        Assert.Equal(HttpStatusCode.Forbidden, renamed.StatusCode);
    }

    [Fact]
    public async Task Concurrent_mutual_demotion_always_leaves_an_admin()
    {
        for (var round = 0; round < 20; round++)
        {
            var (alice, _) = await AuthClient.SignedInAsync(_factory);
            var (carol, _) = await AuthClient.SignedInAsync(_factory);
            using var _a = alice;
            using var _c = carol;
            var org = await OrgClient.CreateAsync(alice, "Standoff");
            await OrgClient.JoinAsync(alice, carol, org);
            var (aliceId, carolId) = (await OrgClient.UserIdAsync(alice), await OrgClient.UserIdAsync(carol));
            await OrgClient.SetRolesAsync(alice, org, carolId, "admin");

            var results = await Task.WhenAll(Status(OrgClient.SetRolesRawAsync(alice, org, carolId)), Status(OrgClient.SetRolesRawAsync(carol, org, aliceId)));
            Assert.All(results, s => Assert.Contains(s, new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Conflict }));
            Assert.Single(results, s => s == HttpStatusCode.OK);
            Assert.Equal(1, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{org}' AND 'admin' = ANY (roles)"));
        }
    }

    [Fact]
    public async Task Removal_and_role_change_of_the_same_member_serialize()
    {
        for (var round = 0; round < 10; round++)
        {
            var (alice, _) = await AuthClient.SignedInAsync(_factory);
            var (bob, _) = await AuthClient.SignedInAsync(_factory);
            var (carol, _) = await AuthClient.SignedInAsync(_factory);
            using var _a = alice;
            using var _b = bob;
            using var _c = carol;
            var org = await OrgClient.CreateAsync(alice, "Crossing roles");
            await OrgClient.JoinAsync(alice, bob, org);
            await OrgClient.JoinAsync(alice, carol, org);
            var (bobId, carolId) = (await OrgClient.UserIdAsync(bob), await OrgClient.UserIdAsync(carol));
            await OrgClient.SetRolesAsync(alice, org, bobId, "admin");

            var (removal, change) = (Status(OrgClient.RemoveAsync(alice, org, carolId)), Status(OrgClient.SetRolesRawAsync(bob, org, carolId, "publisher")));
            Assert.Equal(HttpStatusCode.NoContent, await removal);
            Assert.Contains(await change, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound }); // role-changed then removed, or removed first
            Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{org}' AND user_id = '{carolId}'"));
        }
    }

    private static async Task<System.Text.Json.JsonElement> Detail(HttpClient client, string org)
    {
        using var response = await AuthClient.GetAsync(client, $"{OrgClient.Base}/{org}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await AuthClient.JsonAsync(response);
    }

    private static string?[] Roles(System.Text.Json.JsonElement json) => [.. json.GetProperty("roles").EnumerateArray().Select(r => r.GetString())];

    private int Changes(string userId) => _factory.Logs.Count(l => l.Contains($"Member {userId} roles changed", StringComparison.Ordinal));

    private static async Task<HttpStatusCode> Status(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }
}
