using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// FR-011: sessions end after 30 minutes of inactivity (sliding) and after 12 hours whatever the
/// activity. Time is advanced only after sign-in, so the store's opportunistic purge never sees a
/// future clock and other test classes sharing the database are unaffected.
/// </summary>
public sealed class SessionExpiryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly SettableTimeProvider _time = new();
    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString, configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time)));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Activity_slides_the_idle_window_and_31_idle_minutes_end_the_session()
    {
        var (client, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = client;

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client)); // past half the window: renewed
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client)); // 40 min after sign-in, 20 idle

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
        Assert.Equal(0, await SessionRowsAsync(email));
    }

    [Fact]
    public async Task Continuous_activity_never_extends_a_session_past_12_hours()
    {
        var (client, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = client;

        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(12) - TimeSpan.FromMinutes(20); elapsed += TimeSpan.FromMinutes(20))
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
        }

        _time.Advance(TimeSpan.FromMinutes(21)); // just past 12 h, never idle for more than 21 min
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
        Assert.Equal(0, await SessionRowsAsync(email));
    }

    [Fact]
    public async Task Signing_in_again_when_the_presented_session_is_due_for_sliding_renewal_still_issues_one_live_new_session()
    {
        // GOZ-0003-OBS-01: past half the idle window the presented session would also be refreshed at
        // response time. That refresh must not re-issue the retired key over the new one.
        using var client = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var registered = await AuthClient.RegisterAsync(client, email, AuthClient.Password);
        var previous = AuthClient.SessionCookiePair(registered);

        _time.Advance(TimeSpan.FromMinutes(20));
        using var signedIn = await AuthClient.SignInAsync(client, email, AuthClient.Password);
        Assert.Single(AuthClient.SessionSetCookies(signedIn));
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, previous));
        Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(_factory, AuthClient.SessionCookiePair(signedIn)));
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
        Assert.Equal(1, await SessionRowsAsync(email));
    }

    private static async Task<HttpStatusCode> SessionStatusAsync(HttpClient client)
    {
        using var response = await AuthClient.GetAsync(client, "/api/v1/auth/session");
        return response.StatusCode;
    }

    private Task<long> SessionRowsAsync(string email) => Db.ScalarAsync<long>($"""
        SELECT count(*) FROM gozleme.auth_sessions s JOIN gozleme.users u ON u.id = s.user_id WHERE u.email = '{email}'
        """);

    private sealed class SettableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
