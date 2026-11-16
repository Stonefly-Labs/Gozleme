using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US2 (FR-007–FR-013, SC-005): single-use, revocable, expiring codes with one generic refusal.</summary>
public sealed class InvitationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string InvalidBody = """{"error":"invalid_invitation"}""";

    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_code_is_shown_once_admits_one_person_and_then_reads_like_any_invalid_code()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var _c = carol;
        var org = await OrgClient.CreateAsync(alice, "Invites");
        var (invitationId, code) = await OrgClient.InviteAsync(alice, org);
        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", code);

        using (var listed = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}/invitations"))
        {
            var text = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(invitationId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"code\"", text, StringComparison.Ordinal);
        }

        using (var redeemed = await OrgClient.RedeemAsync(bob, code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)))
        {
            Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
            var json = await AuthClient.JsonAsync(redeemed);
            Assert.Equal(org, json.GetProperty("organizationId").GetString());
            Assert.False(json.GetProperty("alreadyMember").GetBoolean());
        }

        Assert.Contains(org, await OrgClient.IdsAsync(bob));
        var bobMember = Assert.Single(await OrgClient.MembersAsync(bob, org), m => m.GetProperty("isSelf").GetBoolean());
        Assert.Equal("invitation", bobMember.GetProperty("joinedVia").GetString());

        var reused = await RedeemSnapshotAsync(carol, code);
        Assert.Equal((HttpStatusCode.BadRequest, InvalidBody), reused);
        Assert.Empty(await OrgClient.IdsAsync(carol));

        using var after = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}/invitations");
        Assert.Empty((await AuthClient.JsonAsync(after)).GetProperty("invitations").EnumerateArray());
    }

    [Fact]
    public async Task Used_revoked_expired_unknown_and_malformed_codes_are_refused_identically()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var _c = carol;
        var org = await OrgClient.CreateAsync(alice, "Refusals");

        var (_, used) = await OrgClient.InviteAsync(alice, org);
        (await OrgClient.RedeemAsync(bob, used)).Dispose();

        var (revokedId, revoked) = await OrgClient.InviteAsync(alice, org);
        using (var revoke = await OrgClient.RevokeAsync(alice, org, revokedId))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var again = await OrgClient.RevokeAsync(alice, org, revokedId))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode); // no longer open
        }

        var (expiredId, expired) = await OrgClient.InviteAsync(alice, org);
        await GozlemeDatabase.ExecuteAsync($"UPDATE gozleme.invitations SET expires_at = now() - interval '1 minute' WHERE id = '{expiredId}'", Db.MigratorConnectionString);

        var outcomes = new List<(HttpStatusCode, string)>();
        foreach (var code in new[] { used, revoked, expired, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ", "not a code", string.Empty, new string('A', 500) })
        {
            outcomes.Add(await RedeemSnapshotAsync(carol, code));
        }

        Assert.All(outcomes, o => Assert.Equal((HttpStatusCode.BadRequest, InvalidBody), o));
        Assert.Empty(await OrgClient.IdsAsync(carol));
    }

    [Fact]
    public async Task An_existing_member_redeeming_changes_nothing_and_leaves_the_code_usable()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        var org = await OrgClient.CreateAsync(alice, "Already");
        var (_, code) = await OrgClient.InviteAsync(alice, org);

        using (var self = await OrgClient.RedeemAsync(alice, code))
        {
            Assert.Equal(HttpStatusCode.OK, self.StatusCode);
            Assert.True((await AuthClient.JsonAsync(self)).GetProperty("alreadyMember").GetBoolean());
        }

        Assert.Single(await OrgClient.MembersAsync(alice, org));
        using var bobJoin = await OrgClient.RedeemAsync(bob, code);
        Assert.Equal(HttpStatusCode.OK, bobJoin.StatusCode);
        Assert.False((await AuthClient.JsonAsync(bobJoin)).GetProperty("alreadyMember").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_redemptions_of_one_code_admit_exactly_one_account()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Race");
        var (_, code) = await OrgClient.InviteAsync(alice, org);
        var racers = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            racers.Add((await AuthClient.SignedInAsync(_factory)).Client);
        }

        var results = await Task.WhenAll(racers.Select(c => RedeemSnapshotAsync(c, code)));
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.Equal(5, results.Count(r => r == (HttpStatusCode.BadRequest, InvalidBody)));
        Assert.Equal(2, (await OrgClient.MembersAsync(alice, org)).Length);
        racers.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task Only_a_32_byte_hash_is_stored()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Hashed");
        var (id, code) = await OrgClient.InviteAsync(alice, org);
        Assert.Equal(32, await Db.ScalarAsync<int>($"SELECT octet_length(code_hash) FROM gozleme.invitations WHERE id = '{id}'"));
        var row = await Db.ScalarAsync<string>($"SELECT row_to_json(i)::text FROM gozleme.invitations i WHERE id = '{id}'");
        Assert.DoesNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal), row.ToUpperInvariant(), StringComparison.Ordinal);
        Assert.DoesNotContain(code, row, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expiry_follows_the_configured_lifetime()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Organizations:InvitationExpiryHours"] = "2" });
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Lifetime");
        using var response = await OrgClient.InviteRawAsync(alice, org);
        var json = await AuthClient.JsonAsync(response);
        var lifetime = json.GetProperty("expiresAt").GetDateTimeOffset() - json.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromHours(2), lifetime);
    }

    [Fact]
    public async Task Redemption_is_rate_limited_per_account()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Organizations:RedeemPerMinute"] = "10" });
        var (mallory, _) = await AuthClient.SignedInAsync(factory);
        var (other, _) = await AuthClient.SignedInAsync(factory);
        using var _m = mallory;
        using var _o = other;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await RedeemSnapshotAsync(mallory, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ")).Status);
        }

        using var limited = await OrgClient.RedeemAsync(mallory, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("""{"error":"rate_limited"}""", await limited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Partitioned by account: another account is unaffected.
        Assert.Equal(HttpStatusCode.BadRequest, (await RedeemSnapshotAsync(other, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ")).Status);
    }

    private static async Task<(HttpStatusCode Status, string Body)> RedeemSnapshotAsync(HttpClient client, string code)
    {
        using var response = await OrgClient.RedeemAsync(client, code);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
