using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US3 (FR-007, FR-020, SC-004): failures never reveal whether an account exists or is locked.</summary>
public sealed class EnumerationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Wrong_password_unknown_email_and_locked_account_are_indistinguishable()
    {
        var (owner, email) = await AuthClient.SignedInAsync(_factory);
        owner.Dispose();
        var (_, lockedEmail) = await AuthClient.SignedInAsync(_factory);
        using var client = AuthClient.Create(_factory);
        for (var i = 0; i < 5; i++)
        {
            (await AuthClient.SignInAsync(client, lockedEmail, "wrong-password-attempt")).Dispose();
        }

        var responses = new List<(HttpStatusCode Status, string Body, string? Cookie)>();
        foreach (var (e, p) in new[] { (email, "wrong-password-attempt"), (AuthClient.NewEmail("nobody"), AuthClient.Password), (lockedEmail, AuthClient.Password), ("not-an-email", "x") })
        {
            using var response = await AuthClient.SignInAsync(client, e, p);
            responses.Add((response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), AuthClient.SessionSetCookie(response)));
        }

        Assert.All(responses, r =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
            Assert.Equal("""{"error":"invalid_credentials"}""", r.Body);
            Assert.Null(r.Cookie);
        });
    }

    [Fact]
    public async Task Duplicate_registration_does_not_confirm_the_account_exists()
    {
        var (owner, email) = await AuthClient.SignedInAsync(_factory);
        owner.Dispose();
        using var client = AuthClient.Create(_factory);
        using var response = await AuthClient.RegisterAsync(client, email, "a-different-long-passphrase");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("""{"error":"registration_failed"}""", body);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exist", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taken", body, StringComparison.OrdinalIgnoreCase);
    }
}
