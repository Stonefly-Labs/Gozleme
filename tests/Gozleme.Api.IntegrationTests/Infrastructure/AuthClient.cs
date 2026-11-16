using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the auth API like the web tier does: HTTPS (so <c>Secure</c> cookies round-trip), a cookie
/// container and the <c>x-gozleme-client: web</c> header. Emails are unique synthetic <c>.test</c> addresses.
/// </summary>
public static class AuthClient
{
    public const string Password = "it-correct-horse-battery";
    public const string CookieName = "gozleme_session";

    public static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@it.gozleme.test";

    public static HttpClient Create(GozlemeApiFactory factory, bool webHeader = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (webHeader)
        {
            client.DefaultRequestHeaders.Add("x-gozleme-client", "web");
        }

        return client;
    }

    /// <summary>A client signed in as a freshly registered account.</summary>
    public static async Task<(HttpClient Client, string Email)> SignedInAsync(GozlemeApiFactory factory, string? email = null)
    {
        var client = Create(factory);
        email ??= NewEmail();
        using var response = await RegisterAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, email);
    }

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string? email, string? password) =>
        client.PostAsJsonAsync(new Uri("/api/v1/auth/register", UriKind.Relative), new { email, password }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> SignInAsync(HttpClient client, string? email, string? password) =>
        client.PostAsJsonAsync(new Uri("/api/v1/auth/sign-in", UriKind.Relative), new { email, password }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path) =>
        client.PostAsync(new Uri(path, UriKind.Relative), null, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    /// <summary>The raw <c>Set-Cookie</c> header for the session cookie, if any.</summary>
    public static string? SessionSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal)) : null;

    /// <summary>Every <c>Set-Cookie</c> header that writes or deletes the session cookie.</summary>
    public static string[] SessionSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Where(v => v.StartsWith(CookieName, StringComparison.Ordinal))] : [];

    /// <summary>The <c>name=value</c> pair, for replaying a captured cookie from another client.</summary>
    public static string SessionCookiePair(HttpResponseMessage response) =>
        SessionSetCookie(response)?.Split(';')[0] ?? throw new InvalidOperationException("No session cookie was set.");

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    /// <summary>A request that replays a captured cookie without any cookie container.</summary>
    public static async Task<HttpStatusCode> ReplayAsync(GozlemeApiFactory factory, string cookiePair, string path = "/api/v1/auth/session")
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("Cookie", cookiePair);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
