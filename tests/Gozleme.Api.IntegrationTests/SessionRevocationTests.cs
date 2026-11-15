using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US2 (FR-012, FR-016, FR-017): sign-out and sign-out-everywhere revoke server-side, immediately.</summary>
public sealed class SessionRevocationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Sign_out_ends_only_that_session_and_a_replayed_cookie_is_anonymous()
    {
        using var first = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var registered = await AuthClient.RegisterAsync(first, email, AuthClient.Password);
        var firstPair = AuthClient.SessionCookiePair(registered);
        using var second = AuthClient.Create(_factory);
        (await AuthClient.SignInAsync(second, email, AuthClient.Password)).Dispose();

        using var signOut = await AuthClient.PostAsync(first, "/api/v1/auth/sign-out");
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Contains("expires=thu, 01 jan 1970", AuthClient.SessionSetCookie(signOut)!.ToLowerInvariant(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.GetAsync(first, "/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, firstPair));
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.GetAsync(second, "/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_session_of_the_account()
    {
        using var first = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var registered = await AuthClient.RegisterAsync(first, email, AuthClient.Password);
        using var second = AuthClient.Create(_factory);
        using var secondSignIn = await AuthClient.SignInAsync(second, email, AuthClient.Password);
        var secondPair = AuthClient.SessionCookiePair(secondSignIn);
        var (other, _) = await AuthClient.SignedInAsync(_factory);
        using var _ = other;

        using var all = await AuthClient.PostAsync(first, "/api/v1/auth/sign-out-all");
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.GetAsync(first, "/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.GetAsync(second, "/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, secondPair));
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.GetAsync(other, "/api/v1/auth/session")).StatusCode); // other accounts unaffected

        using var again = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(again, email, AuthClient.Password)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_sign_out_is_idempotent_and_sign_out_everywhere_requires_a_session()
    {
        using var anonymous = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.NoContent, (await AuthClient.PostAsync(anonymous, "/api/v1/auth/sign-out")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.PostAsync(anonymous, "/api/v1/auth/sign-out-all")).StatusCode);
    }
}
