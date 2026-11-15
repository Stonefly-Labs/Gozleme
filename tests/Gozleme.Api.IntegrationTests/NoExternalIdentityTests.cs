using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Gozleme.Api.IntegrationTests;

/// <summary>GOZ-0001 boundary (FR-028, US5): no external OIDC/OAuth/SAML identity provider exists yet.</summary>
public sealed class NoExternalIdentityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Only_the_local_application_cookie_scheme_is_registered()
    {
        _ = _factory.Server; // build the host
        var schemes = await _factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.Equal(["Identity.Application"], schemes.Select(s => s.Name));
    }

    [Theory]
    [InlineData("GET", "/signin-oidc")]
    [InlineData("GET", "/signin-google")]
    [InlineData("GET", "/signin-microsoft")]
    [InlineData("GET", "/.well-known/openid-configuration")]
    [InlineData("GET", "/api/v1/auth/external/github")]
    [InlineData("POST", "/api/v1/auth/external-login")]
    public async Task External_sign_in_and_callback_paths_do_not_exist(string method, string path)
    {
        using var client = AuthClient.Create(_factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Options_advertise_no_external_providers_and_no_external_logins_are_stored()
    {
        using var client = AuthClient.Create(_factory);
        var (signedIn, _) = await AuthClient.SignedInAsync(_factory);
        signedIn.Dispose();
        using var options = await AuthClient.GetAsync(client, "/api/v1/auth/options");
        Assert.Equal(0, (await AuthClient.JsonAsync(options)).GetProperty("externalProviders").GetArrayLength());
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM gozleme.user_logins"));
    }
}
