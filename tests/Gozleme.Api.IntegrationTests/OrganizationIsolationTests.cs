using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// US1/US4 (FR-005, FR-006, FR-021, SC-003): non-members, unknown ids and malformed ids are
/// indistinguishable; a tampered or stale context grants nothing; cross-site writes are refused.
/// </summary>
public sealed class OrganizationIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/{org}" },
        { "PATCH", "/{org}" },
        { "POST", "/{org}/select" },
        { "GET", "/{org}/members" },
        { "DELETE", "/{org}/members/{other}" },
        { "GET", "/{org}/invitations" },
        { "POST", "/{org}/invitations" },
        { "DELETE", "/{org}/invitations/{other}" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Non_member_unknown_and_malformed_ids_get_identical_empty_404s(string method, string route)
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (mallory, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _m = mallory;
        var org = await OrgClient.CreateAsync(alice, "Private org");
        var (invitationId, _) = await OrgClient.InviteAsync(alice, org);
        var aliceId = await OrgClient.UserIdAsync(alice);
        object? body = method == "PATCH" ? new { displayName = "Hijacked" } : null;

        string Path(string id) => OrgClient.Base + route.Replace("{org}", id, StringComparison.Ordinal)
            .Replace("{other}", route.Contains("members", StringComparison.Ordinal) ? aliceId : invitationId, StringComparison.Ordinal);

        var existing = await OrgClient.SnapshotAsync(mallory, new HttpMethod(method), Path(org), body);
        var unknown = await OrgClient.SnapshotAsync(mallory, new HttpMethod(method), Path(Guid.NewGuid().ToString()), body);
        var malformed = await OrgClient.SnapshotAsync(mallory, new HttpMethod(method), Path("not-a-uuid"), body);

        Assert.Equal((HttpStatusCode.NotFound, string.Empty, true), existing);
        Assert.Equal(existing, unknown);
        Assert.Equal(existing, malformed);

        // Nothing changed for the real members.
        using var get = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}");
        Assert.Equal("Private org", (await AuthClient.JsonAsync(get)).GetProperty("displayName").GetString());
        Assert.Single(await OrgClient.MembersAsync(alice, org));
        using var invitations = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}/invitations");
        Assert.Single((await AuthClient.JsonAsync(invitations)).GetProperty("invitations").EnumerateArray());
        Assert.Empty(await OrgClient.IdsAsync(mallory));
    }

    [Fact]
    public async Task Selecting_another_tenants_organization_never_makes_it_the_active_context()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (mallory, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _m = mallory;
        var target = await OrgClient.CreateAsync(alice, "Target");
        var own = await OrgClient.CreateAsync(mallory, "Own");

        using var select = await OrgClient.SelectAsync(mallory, target);
        Assert.Equal(HttpStatusCode.NotFound, select.StatusCode);
        Assert.Equal(own, await OrgClient.ActiveAsync(mallory));
        Assert.Equal([own], await OrgClient.IdsAsync(mallory));
    }

    [Fact]
    public async Task A_removed_members_context_falls_back_to_a_real_organization_or_none()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        var shared = await OrgClient.CreateAsync(alice, "Shared");
        await OrgClient.JoinAsync(alice, bob, shared);
        Assert.Equal(shared, await OrgClient.ActiveAsync(bob)); // joining selects

        (await OrgClient.RemoveAsync(alice, shared, await OrgClient.UserIdAsync(bob))).Dispose();
        Assert.Null(await OrgClient.ActiveAsync(bob));

        var bobs = await OrgClient.CreateAsync(bob, "Bob's");
        await OrgClient.JoinAsync(alice, bob, shared);
        (await OrgClient.RemoveAsync(alice, shared, await OrgClient.UserIdAsync(bob))).Dispose();
        Assert.Equal(bobs, await OrgClient.ActiveAsync(bob));
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("PATCH", "/{org}")]
    [InlineData("POST", "/{org}/select")]
    [InlineData("POST", "/{org}/invitations")]
    [InlineData("DELETE", "/{org}/invitations/{org}")]
    [InlineData("DELETE", "/{org}/members/{org}")]
    public async Task Unsafe_calls_without_the_web_client_header_are_refused(string method, string route)
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Guarded");
        alice.DefaultRequestHeaders.Remove("x-gozleme-client");
        using (var request = new HttpRequestMessage(new HttpMethod(method), new Uri(OrgClient.Base + route.Replace("{org}", org, StringComparison.Ordinal), UriKind.Relative)))
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(new { displayName = "Cross-site" });
            using var response = await alice.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        alice.DefaultRequestHeaders.Add("x-gozleme-client", "web");
        Assert.Equal([org], await OrgClient.IdsAsync(alice));
        Assert.Single(await OrgClient.MembersAsync(alice, org));
        using var get = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}");
        Assert.Equal("Guarded", (await AuthClient.JsonAsync(get)).GetProperty("displayName").GetString());
    }
}
