using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US1 (FR-001–FR-005, FR-020): create, list, switch and rename organizations.</summary>
public sealed class OrganizationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Creating_makes_the_creator_the_only_member_and_the_organization_active()
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _c = client;
        using var response = await OrgClient.CreateRawAsync(client, new { displayName = "  Demo Org Alpha  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await AuthClient.JsonAsync(response);
        var id = Guid.Parse(json.GetProperty("id").GetString()!);
        Assert.Equal(7, id.Version);
        Assert.Equal("Demo Org Alpha", json.GetProperty("displayName").GetString());

        var members = await OrgClient.MembersAsync(client, id.ToString());
        var member = Assert.Single(members);
        Assert.Equal("creator", member.GetProperty("joinedVia").GetString());
        Assert.True(member.GetProperty("isSelf").GetBoolean());
        Assert.Equal(id.ToString(), await OrgClient.ActiveAsync(client));
    }

    [Fact]
    public async Task List_holds_exactly_the_callers_organizations_in_stable_order_and_select_switches_context()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        Assert.Empty(await OrgClient.IdsAsync(alice));
        Assert.Null(await OrgClient.ActiveAsync(alice));

        var beta = await OrgClient.CreateAsync(alice, "Beta");
        var alpha = await OrgClient.CreateAsync(alice, "alpha");
        var bobs = await OrgClient.CreateAsync(bob, "Bob only");

        Assert.Equal([alpha, beta], await OrgClient.IdsAsync(alice)); // by display name, case-insensitive
        Assert.Equal([bobs], await OrgClient.IdsAsync(bob));
        Assert.Equal(alpha, await OrgClient.ActiveAsync(alice)); // most recently created

        using (var selected = await OrgClient.SelectAsync(alice, beta))
        {
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
        }

        Assert.Equal(beta, await OrgClient.ActiveAsync(alice));
        using var get = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{beta}");
        Assert.Equal("Beta", (await AuthClient.JsonAsync(get)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Rename_keeps_the_id_and_duplicate_names_are_allowed()
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _c = client;
        var first = await OrgClient.CreateAsync(client, "Same name");
        var second = await OrgClient.CreateAsync(client, "Same name");
        Assert.NotEqual(first, second);

        using var renamed = await OrgClient.RenameAsync(client, first, "Renamed");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var json = await AuthClient.JsonAsync(renamed);
        Assert.Equal(first, json.GetProperty("id").GetString());
        Assert.Equal("Renamed", json.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007name")]
    [InlineData(null)]
    public async Task Invalid_names_are_rejected_with_field_errors_and_nothing_changes(string? name)
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _c = client;
        var org = await OrgClient.CreateAsync(client, "Original");
        foreach (var response in new[] { await OrgClient.CreateRawAsync(client, new { displayName = name }), await OrgClient.RenameAsync(client, org, name) })
        {
            using var _r = response;
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await AuthClient.JsonAsync(response);
            Assert.Equal("validation_failed", json.GetProperty("error").GetString());
            Assert.True(json.GetProperty("fields").TryGetProperty("displayName", out _));
        }

        Assert.Equal([org], await OrgClient.IdsAsync(client));
        using var get = await AuthClient.GetAsync(client, $"{OrgClient.Base}/{org}");
        Assert.Equal("Original", (await AuthClient.JsonAsync(get)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Names_over_100_characters_are_rejected()
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _c = client;
        using var response = await OrgClient.CreateRawAsync(client, new { displayName = new string('x', 101) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("GET", "/{org}")]
    [InlineData("PATCH", "/{org}")]
    [InlineData("POST", "/{org}/select")]
    [InlineData("GET", "/{org}/members")]
    [InlineData("DELETE", "/{org}/members/{org}")]
    [InlineData("GET", "/{org}/invitations")]
    [InlineData("POST", "/{org}/invitations")]
    [InlineData("DELETE", "/{org}/invitations/{org}")]
    public async Task Anonymous_callers_are_refused(string method, string suffix)
    {
        using var client = AuthClient.Create(_factory);
        var (status, body, _) = await OrgClient.SnapshotAsync(client, new HttpMethod(method), OrgClient.Base + suffix.Replace("{org}", Guid.NewGuid().ToString(), StringComparison.Ordinal), method is "GET" or "DELETE" ? null : new { displayName = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Anonymous_redemption_is_refused()
    {
        using var client = AuthClient.Create(_factory);
        using var response = await OrgClient.RedeemAsync(client, "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
