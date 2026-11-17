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

    /// <summary><c>PUT …/members/{userId}/roles</c> (GOZ-0004); <c>member</c> is added by the server.</summary>
    public static Task<HttpResponseMessage> SetRolesRawAsync(HttpClient client, string orgId, string userId, params string[] roles) =>
        client.PutAsJsonAsync(new Uri($"{Base}/{orgId}/members/{userId}/roles", UriKind.Relative), new { roles }, TestContext.Current.CancellationToken);

    public static async Task SetRolesAsync(HttpClient client, string orgId, string userId, params string[] roles)
    {
        using var response = await SetRolesRawAsync(client, orgId, userId, roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

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

    /// <summary>Invites <paramref name="joiner"/> into <paramref name="orgId"/> and redeems; <paramref name="member"/> must be an admin, and the joiner holds member only.</summary>
    public static async Task JoinAsync(HttpClient member, HttpClient joiner, string orgId)
    {
        var (_, code) = await InviteAsync(member, orgId);
        using var response = await RedeemAsync(joiner, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A run-unique handle (GOZ-0005): handles are permanent, so tests never reuse one.</summary>
    public static string NewHandle(string prefix = "it") => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(39, prefix.Length + 33)];

    public static string Publishers(string orgId) => $"{Base}/{orgId}/publishers";

    public static string Publisher(string orgId, string publisherId) => $"{Publishers(orgId)}/{publisherId}";

    public static string Capabilities(string orgId, string publisherId) => $"{Publisher(orgId, publisherId)}/capabilities";

    public static string Capability(string orgId, string publisherId, string capabilityId) => $"{Capabilities(orgId, publisherId)}/{capabilityId}";

    public static Task<HttpResponseMessage> RegisterPublisherRawAsync(HttpClient client, string orgId, string? handle, string? displayName = "IT Publisher") =>
        client.PostAsJsonAsync(new Uri(Publishers(orgId), UriKind.Relative), new { handle, displayName }, TestContext.Current.CancellationToken);

    /// <summary>Registers a publisher (the caller must hold admin) and returns its JSON.</summary>
    public static async Task<JsonElement> RegisterPublisherAsync(HttpClient client, string orgId, string? handle = null, string displayName = "IT Publisher")
    {
        using var response = await RegisterPublisherRawAsync(client, orgId, handle ?? NewHandle(), displayName);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await AuthClient.JsonAsync(response);
    }

    public static Task<HttpResponseMessage> RenamePublisherAsync(HttpClient client, string orgId, string publisherId, object body) =>
        client.PatchAsJsonAsync(new Uri(Publisher(orgId, publisherId), UriKind.Relative), body, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> CreateCapabilityRawAsync(HttpClient client, string orgId, string publisherId, string? name, string? displayName = "IT Capability") =>
        client.PostAsJsonAsync(new Uri(Capabilities(orgId, publisherId), UriKind.Relative), new { name, displayName }, TestContext.Current.CancellationToken);

    /// <summary>Creates a capability (the caller must hold publisher) and returns its JSON.</summary>
    public static async Task<JsonElement> CreateCapabilityAsync(HttpClient client, string orgId, string publisherId, string name = "weather-lookup", string displayName = "IT Capability")
    {
        using var response = await CreateCapabilityRawAsync(client, orgId, publisherId, name, displayName);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await AuthClient.JsonAsync(response);
    }

    public static Task<HttpResponseMessage> RenameCapabilityAsync(HttpClient client, string orgId, string publisherId, string capabilityId, object body) =>
        client.PatchAsJsonAsync(new Uri(Capability(orgId, publisherId, capabilityId), UriKind.Relative), body, TestContext.Current.CancellationToken);

    public static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await AuthClient.GetAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await AuthClient.JsonAsync(response);
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
