using System.Net;
using System.Net.Http.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US2/US3 (FR-014): unsafe identity calls without the web-tier header are refused and change nothing.</summary>
public sealed class CsrfTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/v1/auth/register", null)]
    [InlineData("/api/v1/auth/sign-in", null)]
    [InlineData("/api/v1/auth/sign-out", null)]
    [InlineData("/api/v1/auth/sign-out-all", null)]
    [InlineData("/api/v1/auth/sign-in", "evil")]
    [InlineData("/api/v1/auth/sign-out-all", "WEB")]
    public async Task Unsafe_calls_without_the_web_client_header_are_forbidden(string path, string? headerValue)
    {
        var (signedIn, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = signedIn;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(new { email, password = AuthClient.Password }),
        };
        request.Headers.Remove("x-gozleme-client");
        signedIn.DefaultRequestHeaders.Remove("x-gozleme-client");
        if (headerValue is not null)
        {
            request.Headers.Add("x-gozleme-client", headerValue);
        }

        using var response = await signedIn.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"forbidden"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(AuthClient.SessionSetCookie(response));
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.GetAsync(signedIn, "/api/v1/auth/session")).StatusCode); // session unaffected
    }
}
