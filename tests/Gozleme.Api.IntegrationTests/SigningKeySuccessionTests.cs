using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Revisions;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T053 (SC-008, FR-007a, contracts/signing-keys.md): operator <c>signing-key-succession</c> for one publisher
/// (<c>lost</c>, <c>compromised</c>) and for every undecryptable key after a KEK change; retired keys keep verifying
/// and new revisions use the successor; a racing publication never fails and never leaves two active keys; it works
/// for a suspended publisher; exit codes 0/1/2/64; opaque IDs only; and <c>gozleme_app</c> cannot insert a later
/// generation or a retirement. The class owns its database: <c>--undecryptable</c> spans every publisher.
/// </summary>
public sealed partial class SigningKeySuccessionTests : IAsyncLifetime
{
    private static readonly string OtherKek = Convert.ToBase64String(Encoding.ASCII.GetBytes("a-different-kek-after-a-change!!"));

    private readonly ConcurrentQueue<string> _logs = new();
    private GozlemeDatabase _db = null!;
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _s = await RevisionScenario.CreateAsync(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _s.DisposeAsync();
        await _db.DisposeAsync();
    }

    [GeneratedRegex("^(?<p>[0-9a-f-]{36}) retired (?<r>[0-9a-f-]{36}) successor (?<s>[0-9a-f-]{36})$", RegexOptions.Multiline)]
    private static partial Regex OutputLine();

    private async Task<System.Text.Json.JsonElement[]> KeysAsync(string publisherId) =>
        [.. (await RevisionClient.GetAsync(_s.Erin, RevisionClient.Keys(_s.OrgA, publisherId))).Body.GetProperty("keys").EnumerateArray()];

    [Theory]
    [InlineData("lost")]
    [InlineData("compromised")]
    public async Task Succession_retires_the_active_key_and_new_revisions_use_the_successor(string reason)
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, first) = await _s.PublishedAsync("1.0.0", publisherId);
        var gen1 = Assert.Single(await KeysAsync(publisherId));

        var (code, stdout, stderr) = await _s.SuccessionAsync(_logs, null, "--publisher", publisherId, "--reason", reason);
        Assert.True(code == SigningKeyOperatorCommands.Ok, stderr);
        var line = OutputLine().Match(stdout.TrimEnd());
        Assert.True(line.Success, stdout);
        Assert.Equal(publisherId, line.Groups["p"].Value);
        Assert.Equal(gen1.GetProperty("id").GetString(), line.Groups["r"].Value);

        var keys = await KeysAsync(publisherId);
        Assert.Equal([1, 2], keys.Select(k => k.GetProperty("generation").GetInt32()));
        Assert.Equal(("retired", reason, line.Groups["s"].Value), (keys[0].GetProperty("status").GetString(), keys[0].GetProperty("retiredReason").GetString(), keys[0].GetProperty("successorId").GetString()));
        Assert.Equal("active", keys[1].GetProperty("status").GetString());

        // The earlier revision still verifies and reports its key as retired.
        var (_, reread) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, first.GetProperty("id").GetString()!, publisherId));
        var verification = reread.GetProperty("verification");
        Assert.Equal("verified", verification.GetProperty("status").GetString());
        Assert.Equal(("retired", reason), (verification.GetProperty("key").GetProperty("status").GetString(), verification.GetProperty("key").GetProperty("retiredReason").GetString()));

        var second = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId, publisherId), RevisionClient.Envelope(canonical, "1.1.0"));
        Assert.Equal(keys[1].GetProperty("keyId").GetString(), second.GetProperty("attestation").GetProperty("keyId").GetString());

        // Opaque IDs only: no handle, KEK or key material in the output or the audit event.
        Assert.Contains(_logs, l => l.Contains("Gozleme.Security.Audit: Operator retired signing key", StringComparison.Ordinal) && l.Contains(publisherId, StringComparison.Ordinal));
        foreach (var text in _logs.Append(stdout).Append(stderr))
        {
            Assert.DoesNotContain(handle, text, StringComparison.Ordinal);
            Assert.DoesNotContain(RevisionClient.TestKek, text, StringComparison.Ordinal);
            Assert.DoesNotContain(keys[1].GetProperty("keyId").GetString()!, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task After_a_KEK_change_publication_is_unavailable_until_undecryptable_keys_are_succeeded()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, first) = await _s.PublishedAsync("1.0.0", publisherId);
        await using var changed = new GozlemeApiFactory(_db.AppConnectionString, settings: new Dictionary<string, string?>
        {
            ["Gozleme:Signing:KeyEncryptionKey"] = OtherKek,
            ["Gozleme:Manifests:ValidatePerMinute"] = "10000",
        });
        using var carol = (await TwoTenants.AddPublisherMemberAsync(changed, _s.Alice, Guid.Parse(_s.OrgA))).Client;
        var path = _s.Revisions(capabilityId, publisherId);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}"""), await ManifestClient.SendAsync(carol, path, RevisionClient.Envelope(canonical, "2.0.0")));
        Assert.Equal(1L, await _db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));

        // Reads and verification keep working under the new KEK.
        var (_, read) = await RevisionClient.GetAsync(carol, _s.Revision(capabilityId, first.GetProperty("id").GetString()!, publisherId));
        Assert.Equal("verified", read.GetProperty("verification").GetProperty("status").GetString());

        var (code, stdout, stderr) = await _s.SuccessionAsync(_logs, OtherKek, "--undecryptable", "--reason", "lost");
        Assert.True(code == SigningKeyOperatorCommands.Ok, stderr);
        Assert.Contains(OutputLine().Matches(stdout), m => m.Groups["p"].Value == publisherId);
        var published = await RevisionClient.PublishedAsync(carol, path, RevisionClient.Envelope(canonical, "2.0.0"));
        Assert.Equal(2, published.GetProperty("sequence").GetInt32());

        // Nothing left to do is success with no output.
        (code, stdout, _) = await _s.SuccessionAsync(_logs, OtherKek, "--undecryptable", "--reason", "lost");
        Assert.Equal((SigningKeyOperatorCommands.Ok, ""), (code, stdout));
    }

    [Fact]
    public async Task A_publication_racing_a_succession_never_fails_and_leaves_one_active_key()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, _) = await _s.PublishedAsync("0.1.0", publisherId);
        var path = _s.Revisions(capabilityId, publisherId);
        var publications = Enumerable.Range(1, 8).Select(i => ManifestClient.SendAsync(_s.Carol, path, RevisionClient.Envelope(canonical, $"1.{i}.0"))).ToList();
        var succession = _s.SuccessionAsync(null, null, "--publisher", publisherId, "--reason", "compromised");
        Assert.Equal(SigningKeyOperatorCommands.Ok, (await succession).Code);
        Assert.All(await Task.WhenAll(publications), r => Assert.Equal(HttpStatusCode.Created, r.Status));

        Assert.Equal(1L, await _db.ScalarAsync<long>($"""
            SELECT count(*) FROM gozleme.publisher_signing_keys k
            WHERE k.publisher_id = '{publisherId}' AND NOT EXISTS (SELECT 1 FROM gozleme.signing_key_retirements r WHERE r.key_id = k.id)
            """));
        var (_, list) = await RevisionClient.GetAsync(_s.Erin, path);
        Assert.All(list.GetProperty("revisions").EnumerateArray(), r => Assert.Equal("verified", r.GetProperty("verification").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Succession_works_for_a_suspended_publisher_and_publication_stays_refused()
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, _) = await _s.PublishedAsync("1.0.0", publisherId);
        await _s.ReclaimAsync(handle);
        Assert.Equal(SigningKeyOperatorCommands.Ok, (await _s.SuccessionAsync(null, null, "--publisher", publisherId, "--reason", "lost")).Code);
        Assert.Equal(2, (await KeysAsync(publisherId)).Length);
        Assert.Equal(HttpStatusCode.Conflict, (await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId, publisherId), RevisionClient.Envelope(canonical, "2.0.0"))).Status);
    }

    [Fact]
    public async Task Exit_codes_follow_the_contract_and_nothing_is_committed_on_failure()
    {
        var (noKey, _) = await _s.NewPublisherAsync();
        Assert.Equal(SigningKeyOperatorCommands.UnknownTarget, (await _s.SuccessionAsync(_logs, null, "--publisher", Guid.NewGuid().ToString(), "--reason", "lost")).Code);
        Assert.Equal(SigningKeyOperatorCommands.UnknownTarget, (await _s.SuccessionAsync(_logs, null, "--publisher", noKey, "--reason", "lost")).Code);
        Assert.Contains(_logs, l => l.Contains("Gozleme.Security.Audit: Operator signing key succession refused (unknown_target)", StringComparison.Ordinal));

        string[][] usage =
        [
            [], ["--publisher", noKey], ["--reason", "lost"], ["--publisher", noKey, "--reason", "stolen"], ["--publisher", "nope", "--reason", "lost"],
            ["--undecryptable", "--reason", "compromised"], ["--undecryptable", "--publisher", noKey, "--reason", "lost"], ["--publisher", noKey, "--reason", "lost", "--extra"],
        ];
        foreach (var args in usage)
        {
            var (code, _, stderr) = await _s.SuccessionAsync(null, null, args);
            Assert.True(code == SigningKeyOperatorCommands.Usage, string.Join(' ', args));
            Assert.StartsWith("Usage: signing-key-succession", stderr, StringComparison.Ordinal);
        }

        var (publisherId, _) = await _s.NewPublisherAsync();
        await _s.PublishedAsync("1.0.0", publisherId);
        Assert.Equal(SigningKeyOperatorCommands.Failed, (await _s.SuccessionAsync(null, "", "--publisher", publisherId, "--reason", "lost")).Code);
        Assert.Equal(SigningKeyOperatorCommands.Failed, (await _s.SuccessionAsync(null, "bm90LTMyLWJ5dGVz", "--publisher", publisherId, "--reason", "lost")).Code);
        Assert.Single(await KeysAsync(publisherId));
    }

    [Fact]
    public async Task The_app_login_cannot_insert_a_later_generation_or_a_retirement()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        await _s.PublishedAsync("1.0.0", publisherId);
        var gen1 = (await KeysAsync(publisherId))[0].GetProperty("id").GetString();
        const string pk = "decode(repeat('ab', 32), 'hex')";
        var insertKey = $"""
            INSERT INTO gozleme.publisher_signing_keys (id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key, created_at, created_by)
            VALUES (gen_random_uuid(), '{_s.OrgA}', '{publisherId}', 2, 'sha256:' || encode(sha256({pk}), 'hex'), 'Ed25519', {pk}, '\x01'::bytea || decode(repeat('00', 60), 'hex'), now(), '{_s.CarolId}')
            """;
        var retire = $"INSERT INTO gozleme.signing_key_retirements (key_id, organization_id, successor_key_id, reason, retired_at) VALUES ('{gen1}', '{_s.OrgA}', '{gen1}', 'lost', now())";
        foreach (var login in new[] { _db.AppConnectionString, _db.MigratorConnectionString })
        {
            Assert.Equal("42501", await RevisionScenario.FailsAsync(login, RevisionScenario.Context(_s.CarolId), insertKey));
            Assert.Equal("42501", await RevisionScenario.FailsAsync(login, RevisionScenario.Context(_s.CarolId), retire));
        }
    }
}
