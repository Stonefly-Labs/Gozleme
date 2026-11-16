using System.Net;
using System.Net.Http.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// US5 boundary (FR-018, FR-024, research R8), narrowed by GOZ-0004: standing comes only from the
/// built-in role set in <c>memberships.roles</c>; no owner, delegation, policy or custom-role surface
/// exists, and extra privilege fields are ignored.
/// </summary>
public sealed class OrganizationBoundaryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Only_the_membership_role_set_exists_and_no_owner_admin_or_permission_column()
    {
        // GOZ-0004 narrows GOZ-0002-IF-09 deliberately: built-in roles live only in memberships.roles.
        var columns = await fixture.Database.ScalarAsync<string>("""
            SELECT string_agg(table_name || '.' || column_name, ',' ORDER BY table_name, column_name)
            FROM information_schema.columns
            WHERE table_schema = 'gozleme' AND table_name IN ('organizations', 'memberships', 'invitations')
            """);
        Assert.DoesNotMatch("(role|owner|admin|permission|delegat)", columns.Replace("memberships.roles", "", StringComparison.Ordinal));
        Assert.Equal(
            "invitations.code_hash,invitations.created_at,invitations.created_by,invitations.expires_at,invitations.id,invitations.organization_id,"
            + "invitations.revoked_at,invitations.revoked_by,invitations.used_at,invitations.used_by,"
            + "memberships.invitation_id,memberships.joined_at,memberships.joined_via,memberships.organization_id,memberships.roles,memberships.selected_at,memberships.user_id,"
            + "organizations.created_at,organizations.created_by,organizations.display_name,organizations.id,organizations.updated_at",
            columns);
    }

    [Theory]
    [InlineData("PATCH", "/members/{user}")]
    [InlineData("PUT", "/members/{user}")]
    [InlineData("POST", "/members/{user}/role")]
    [InlineData("PUT", "/members/{user}/role")] // GOZ-0004: only PUT /members/{user}/roles exists
    [InlineData("POST", "/members")]
    [InlineData("POST", "/transfer")]
    [InlineData("POST", "/owner")]
    [InlineData("GET", "/admins")]
    [InlineData("POST", "/admins")]
    [InlineData("GET", "/roles")]
    [InlineData("POST", "/roles")]
    [InlineData("GET", "/policies")]
    [InlineData("POST", "/policies")]
    [InlineData("GET", "/permissions")]
    [InlineData("POST", "/grants")]
    [InlineData("POST", "/members/{user}/roles")]
    [InlineData("PATCH", "/members/{user}/roles")]
    [InlineData("DELETE", "/members/{user}/roles")]
    [InlineData("DELETE", "")]
    public async Task Delegation_routes_do_not_exist(string method, string suffix)
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "No delegation");
        var aliceId = await OrgClient.UserIdAsync(alice);
        var (status, _, _) = await OrgClient.SnapshotAsync(alice, new HttpMethod(method), $"{OrgClient.Base}/{org}" + suffix.Replace("{user}", aliceId, StringComparison.Ordinal), new { role = "admin" });
        Assert.Contains(status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Single(await OrgClient.MembersAsync(alice, org)); // and the organization still exists unchanged
    }

    [Fact]
    public async Task No_policy_or_custom_role_surface_exists()
    {
        // FR-016, FR-017: fixed built-in roles evaluated from (roles, action) only; GOZ-0018 owns policies.
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "No policies");
        var aliceId = await OrgClient.UserIdAsync(alice);
        using (var custom = await OrgClient.SetRolesRawAsync(alice, org, aliceId, "admin", "auditor"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, custom.StatusCode);
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema IN ('gozleme', 'gozleme_private') AND table_name ~ '(polic|grant|role|permission)'
            """));

        var parameters = typeof(OrganizationAccess).GetMethod(nameof(OrganizationAccess.AuthorizeAsync))!.GetParameters();
        Assert.Equal(["db", "organizationId", "http", "action", "ct"], parameters.Select(p => p.Name));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(object) || p.ParameterType.Name.Contains("Dictionary", StringComparison.Ordinal));
    }

    private static readonly string[] AdminOnly = ["admin"];

    [Fact]
    public async Task Extra_privilege_fields_are_ignored_and_standing_comes_only_from_roles()
    {
        // GOZ-0004 replaces GOZ-0002 FR-018's "no special standing": the creator holds admin through its
        // role set, and a redeemer holds member only, whatever privilege fields the requests carry.
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var created = await alice.PostAsJsonAsync(new Uri(OrgClient.Base, UriKind.Relative), new { displayName = "Flat", role = "admin", owner = true, isAdmin = true, roles = AdminOnly }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var org = (await AuthClient.JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal(["id", "displayName", "createdAt"], (await AuthClient.JsonAsync(created)).EnumerateObject().Select(p => p.Name));

        var (_, code) = await OrgClient.InviteAsync(alice, org);
        using (var redeem = await bob.PostAsJsonAsync(new Uri("/api/v1/invitations/redeem", UriKind.Relative), new { code, role = "owner", roles = AdminOnly }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        }

        var members = await OrgClient.MembersAsync(alice, org);
        Assert.Equal(["admin", "member"], members.Single(m => m.GetProperty("isSelf").GetBoolean()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["member"], members.Single(m => !m.GetProperty("isSelf").GetBoolean()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // Bob, an ordinary member, can neither remove the creator nor rename.
        using (var removed = await OrgClient.RemoveAsync(bob, org, await OrgClient.UserIdAsync(alice)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
        }

        using var renamed = await OrgClient.RenameAsync(bob, org, "Not allowed");
        Assert.Equal(HttpStatusCode.Forbidden, renamed.StatusCode);
    }

    [Fact]
    public async Task Account_listing_stays_absent()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var response = await AuthClient.GetAsync(alice, "/api/v1/users");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
