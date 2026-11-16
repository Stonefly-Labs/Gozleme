using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>Drives the organizations API (GOZ-0002) with an <see cref="AuthClient"/> session.</summary>
public static class OrgClient
{
    public const string Base = "/api/v1/organizations";

    public static Task<HttpResponseMessage> CreateRawAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync(new Uri(Base, UriKind.Relative), body, TestContext.Current.CancellationToken);

    /// <summary>Creates an organization and returns its id.</summary>
    public static async Task<string> CreateAsync(HttpClient client, string displayName = "IT Org")
    {
        using var response = await CreateRawAsync(client, new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AuthClient.JsonAsync(response)).GetProperty("id").GetString()!;
    }

    public static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await AuthClient.GetAsync(client, Base);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await AuthClient.JsonAsync(response);
    }

    public static async Task<string?> ActiveAsync(HttpClient client) => (await ListAsync(client)).GetProperty("activeOrganizationId").GetString();

    public static async Task<string[]> IdsAsync(HttpClient client) =>
        [.. (await ListAsync(client)).GetProperty("organizations").EnumerateArray().Select(o => o.GetProperty("id").GetString()!)];

    public static Task<HttpResponseMessage> RenameAsync(HttpClient client, string orgId, string? displayName) =>
        client.PatchAsJsonAsync(new Uri($"{Base}/{orgId}", UriKind.Relative), new { displayName }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> SelectAsync(HttpClient client, string orgId) => AuthClient.PostAsync(client, $"{Base}/{orgId}/select");

    public static Task<HttpResponseMessage> InviteRawAsync(HttpClient client, string orgId) => AuthClient.PostAsync(client, $"{Base}/{orgId}/invitations");

    /// <summary>Creates an invitation and returns (invitationId, code).</summary>
    public static async Task<(string Id, string Code)> InviteAsync(HttpClient client, string orgId)
    {
        using var response = await InviteRawAsync(client, orgId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await AuthClient.JsonAsync(response);
        return (json.GetProperty("id").GetString()!, json.GetProperty("code").GetString()!);
    }

    public static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string? code) =>
        client.PostAsJsonAsync(new Uri("/api/v1/invitations/redeem", UriKind.Relative), new { code }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> RevokeAsync(HttpClient client, string orgId, string invitationId) =>
        client.DeleteAsync(new Uri($"{Base}/{orgId}/invitations/{invitationId}", UriKind.Relative), TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string orgId, string userId) =>
        client.DeleteAsync(new Uri($"{Base}/{orgId}/members/{userId}", UriKind.Relative), TestContext.Current.CancellationToken);

    public static async Task<JsonElement[]> MembersAsync(HttpClient client, string orgId)
    {
        using var response = await AuthClient.GetAsync(client, $"{Base}/{orgId}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await AuthClient.JsonAsync(response)).GetProperty("members").EnumerateArray()];
    }

    /// <summary>The signed-in account's opaque id.</summary>
    public static async Task<string> UserIdAsync(HttpClient client)
    {
        using var response = await AuthClient.GetAsync(client, "/api/v1/auth/session");
        return (await AuthClient.JsonAsync(response)).GetProperty("userId").GetString()!;
    }

    /// <summary>Invites <paramref name="joiner"/> into <paramref name="orgId"/> and redeems.</summary>
    public static async Task JoinAsync(HttpClient member, HttpClient joiner, string orgId)
    {
        var (_, code) = await InviteAsync(member, orgId);
        using var response = await RedeemAsync(joiner, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Status and exact body bytes, for existence-parity comparisons.</summary>
    public static async Task<(HttpStatusCode Status, string Body, bool NoStore)> SnapshotAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response.Headers.CacheControl?.NoStore == true);
    }
}
