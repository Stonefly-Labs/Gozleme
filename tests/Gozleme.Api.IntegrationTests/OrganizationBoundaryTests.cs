using System.Net;
using System.Net.Http.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// US5 boundary (FR-018, FR-024, research R8): membership is role-neutral and no administrative
/// delegation exists — no role/owner/admin storage, no delegation routes, extra fields ignored, and
/// the creator holds no special standing.
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
    public async Task No_role_owner_admin_or_permission_column_exists()
    {
        var columns = await fixture.Database.ScalarAsync<string>("""
            SELECT string_agg(table_name || '.' || column_name, ',' ORDER BY table_name, column_name)
            FROM information_schema.columns
            WHERE table_schema = 'gozleme' AND table_name IN ('organizations', 'memberships', 'invitations')
            """);
        Assert.DoesNotMatch("(role|owner|admin|permission|delegat)", columns);
        Assert.Equal(
            "invitations.code_hash,invitations.created_at,invitations.created_by,invitations.expires_at,invitations.id,invitations.organization_id,"
            + "invitations.revoked_at,invitations.revoked_by,invitations.used_at,invitations.used_by,"
            + "memberships.invitation_id,memberships.joined_at,memberships.joined_via,memberships.organization_id,memberships.selected_at,memberships.user_id,"
            + "organizations.created_at,organizations.created_by,organizations.display_name,organizations.id,organizations.updated_at",
            columns);
    }

    [Theory]
    [InlineData("PATCH", "/members/{user}")]
    [InlineData("PUT", "/members/{user}")]
    [InlineData("POST", "/members/{user}/role")]
    [InlineData("PUT", "/members/{user}/role")]
    [InlineData("POST", "/members")]
    [InlineData("POST", "/transfer")]
    [InlineData("POST", "/owner")]
    [InlineData("GET", "/admins")]
    [InlineData("POST", "/admins")]
    [InlineData("GET", "/roles")]
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
    public async Task Extra_privilege_fields_are_ignored_and_the_creator_has_no_special_standing()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var created = await alice.PostAsJsonAsync(new Uri(OrgClient.Base, UriKind.Relative), new { displayName = "Flat", role = "admin", owner = true, isAdmin = true }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var org = (await AuthClient.JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal(["id", "displayName", "createdAt"], (await AuthClient.JsonAsync(created)).EnumerateObject().Select(p => p.Name));

        var (_, code) = await OrgClient.InviteAsync(alice, org);
        using (var redeem = await bob.PostAsJsonAsync(new Uri("/api/v1/invitations/redeem", UriKind.Relative), new { code, role = "owner" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        }

        // Bob, an ordinary member, may remove the creator; the creator then has no access at all.
        using (var removed = await OrgClient.RemoveAsync(bob, org, await OrgClient.UserIdAsync(alice)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var gone = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        using var renamed = await OrgClient.RenameAsync(bob, org, "Still works");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
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
