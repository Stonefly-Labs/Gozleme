using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 US5 in the database (research R2–R4): the handle claim state machine and who may take each step.
/// <c>gozleme_app</c> with an account context may insert <c>held</c> and consume its own organization's earmark;
/// the owner's own login with no context (the operator) may block, earmark, release an unused reservation and
/// reclaim; nobody may do anything else. A suspended publisher is frozen in the database itself (FR-037).
/// </summary>
public sealed class HandleClaimTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Refused = PostgresErrorCodes.InsufficientPrivilege;

    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _carol = null!;
    private HttpClient _bob = null!;
    private Guid _aliceId;
    private Guid _carolId;
    private Guid _bobId;
    private Guid _orgA;
    private Guid _orgB;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = Guid.Parse(await OrgClient.CreateAsync(_alice, "Claims A"));
        _orgB = Guid.Parse(await OrgClient.CreateAsync(_bob, "Claims B"));
        (_carol, _carolId) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, _orgA);
        _aliceId = Guid.Parse(await OrgClient.UserIdAsync(_alice));
        _bobId = Guid.Parse(await OrgClient.UserIdAsync(_bob));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _alice, _carol, _bob })
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task An_earmark_is_consumed_only_by_its_own_organizations_admin()
    {
        var handle = OrgClient.NewHandle("mark");
        await Operator($"INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES (gen_random_uuid(), '{handle}', 'earmarked', '{_orgA}')");
        var consume = $"UPDATE gozleme.handle_claims SET status = 'held', held_at = now(), publisher_id = gen_random_uuid() WHERE handle = '{handle}' AND status = 'earmarked'";
        Assert.Equal(0, await Rows(_bobId, consume)); // another organization's admin: invisible
        Assert.Equal(0, await Rows(_carolId, consume)); // a member without admin
        var registered = await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), handle);
        Assert.Equal("held", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{handle}' AND publisher_id = '{registered.GetProperty("id").GetString()}'"));
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{handle}'"));
    }

    [Fact]
    public async Task Claims_are_visible_only_to_their_organization_and_blocked_rows_to_nobody()
    {
        var blocked = OrgClient.NewHandle("blk");
        await Operator($"INSERT INTO gozleme.handle_claims (id, handle, status) VALUES (gen_random_uuid(), '{blocked}', 'blocked')");
        var held = OrgClient.NewHandle("held");
        await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), held);
        Assert.Equal(1, await Count(_aliceId, $"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{held}'"));
        Assert.Equal(0, await Count(_bobId, $"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{held}'"));
        foreach (var account in new[] { _aliceId, _carolId, _bobId })
        {
            Assert.Equal(0, await Count(account, $"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{blocked}'"));
            Assert.Equal(0, await Count(account, $"SELECT count(*) FROM gozleme.handle_claims WHERE organization_id IS NULL"));
        }
    }

    [Fact]
    public async Task The_app_cannot_block_earmark_or_reclaim()
    {
        var publisher = Guid.Parse((await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString())).GetProperty("id").GetString()!);
        Assert.Equal(Refused, await Fails(_aliceId, $"INSERT INTO gozleme.handle_claims (id, handle, status) VALUES (gen_random_uuid(), '{OrgClient.NewHandle()}', 'blocked')"));
        Assert.Equal(Refused, await Fails(_aliceId, $"INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES (gen_random_uuid(), '{OrgClient.NewHandle()}', 'earmarked', '{_orgA}')"));
        Assert.Equal(0, await Rows(_aliceId, $"UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE publisher_id = '{publisher}'"));
        Assert.Equal("held", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE publisher_id = '{publisher}'"));
    }

    [Fact]
    public async Task The_operator_takes_only_its_own_steps()
    {
        var handle = OrgClient.NewHandle("op");
        var publisher = Guid.Parse((await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), handle)).GetProperty("id").GetString()!);
        var claim = await Db.ScalarAsync<Guid>($"SELECT id FROM gozleme.handle_claims WHERE publisher_id = '{publisher}'");

        // Cannot insert held, release a held handle or change a handle.
        Assert.Equal(Refused, await OperatorFails($"INSERT INTO gozleme.handle_claims (id, handle, status, organization_id, publisher_id, held_at) VALUES (gen_random_uuid(), '{OrgClient.NewHandle()}', 'held', '{_orgA}', '{publisher}', now())"));
        Assert.Equal(Refused, await OperatorFails($"UPDATE gozleme.handle_claims SET status = 'released', ended_at = now(), publisher_id = NULL, held_at = NULL WHERE id = '{claim}'"));
        Assert.Equal(Refused, await OperatorFails($"UPDATE gozleme.handle_claims SET handle = '{OrgClient.NewHandle()}' WHERE id = '{claim}'"));
        Assert.Equal(Refused, await OperatorFails($"UPDATE gozleme.handle_claims SET organization_id = '{_orgB}' WHERE id = '{claim}'"));

        // The operator sees no publisher or capability: its login carries no account context.
        Assert.Equal(0, await Db.AsOwnerAsync<long>("SELECT count(*) FROM gozleme.publishers"));
        Assert.Equal(0, await Db.AsOwnerAsync<long>("SELECT count(*) FROM gozleme.capabilities"));

        // Reclaimed is terminal: no un-reclaim.
        await Operator($"UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE id = '{claim}'");
        Assert.Equal(Refused, await OperatorFails($"UPDATE gozleme.handle_claims SET status = 'held', ended_at = NULL WHERE id = '{claim}'"));
        Assert.Equal(0, await Rows(_aliceId, $"UPDATE gozleme.handle_claims SET status = 'held', ended_at = NULL WHERE id = '{claim}'"));

        // Owner with a member context is neither operator nor member: it cannot reserve or hold.
        Assert.Equal(Refused, await OwnerAsMemberFails(_aliceId, $"INSERT INTO gozleme.handle_claims (id, handle, status) VALUES (gen_random_uuid(), '{OrgClient.NewHandle()}', 'blocked')"));
    }

    [Fact]
    public async Task A_suspended_publisher_is_frozen_in_the_database_and_its_retired_aliases_stay_reserved()
    {
        var handle = OrgClient.NewHandle("frz");
        var publisher = (await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), handle)).GetProperty("id").GetString()!;
        var capability = (await OrgClient.CreateCapabilityAsync(_carol, _orgA.ToString(), publisher, "forecast")).GetProperty("id").GetString()!;
        await Operator($"UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE publisher_id = '{publisher}'");

        Assert.Equal(Refused, await Fails(_carolId,
            $"INSERT INTO gozleme.capabilities (id, organization_id, publisher_id, name, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), '{_orgA}', '{publisher}', 'radar', 'R', now(), '{_carolId}', now())"));
        Assert.Equal(Refused, await Fails(_aliceId, $"UPDATE gozleme.publishers SET display_name = 'R' WHERE id = '{publisher}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.capabilities SET display_name = 'R' WHERE id = '{capability}'"));

        // The next holder of the handle cannot reuse the retired alias; a new name is fine.
        var (bobPublisher, bobPublisherId) = await TwoTenants.AddPublisherMemberAsync(_factory, _bob, _orgB);
        using var _ = bobPublisher;
        var successor = (await OrgClient.RegisterPublisherAsync(_bob, _orgB.ToString(), handle)).GetProperty("id").GetString()!;
        var insert = (string name) => $"INSERT INTO gozleme.capabilities (id, organization_id, publisher_id, name, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), '{_orgB}', '{successor}', '{name}', 'F', now(), '{bobPublisherId}', now())";
        var retired = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(Db.AppConnectionString, Context(bobPublisherId), insert("forecast")));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "alias_reservations_pkey"), (retired.SqlState, retired.ConstraintName));
        Assert.Equal(1, await Rows(bobPublisherId, insert("radar")));
    }

    [Fact]
    public async Task A_publisher_cannot_hold_a_claim_on_a_handle_other_than_its_own()
    {
        // bob's organization holds `victim`; alice tries to give a publisher row carrying `victim` a claim on a free
        // handle, which would let its capabilities reserve `victim/<name>` aliases (converge F1, security finding 1).
        var victim = OrgClient.NewHandle("vic");
        await OrgClient.RegisterPublisherAsync(_bob, _orgB.ToString(), victim);
        var free = OrgClient.NewHandle("free");
        var publisher = Guid.CreateVersion7();
        var insertPublisher = $"INSERT INTO gozleme.publishers (id, organization_id, handle, display_name, created_at, created_by, updated_at) VALUES ('{publisher}', '{_orgA}', '{victim}', 'X', now(), '{_aliceId}', now())";
        var held = $"{insertPublisher}; INSERT INTO gozleme.handle_claims (id, handle, status, organization_id, publisher_id, held_at) VALUES (gen_random_uuid(), '{free}', 'held', '{_orgA}', '{publisher}', now())";
        Assert.Equal(Refused, await Fails(_aliceId, held));
        Assert.Equal(Refused, await OwnerAsMemberFails(_aliceId, held));

        var earmarked = OrgClient.NewHandle("mark");
        await Operator($"INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES (gen_random_uuid(), '{earmarked}', 'earmarked', '{_orgA}')");
        var consume = $"{insertPublisher}; UPDATE gozleme.handle_claims SET status = 'held', held_at = now(), publisher_id = '{publisher}' WHERE handle = '{earmarked}' AND status = 'earmarked'";
        Assert.Equal(Refused, await Fails(_aliceId, consume));
        Assert.Equal(Refused, await OwnerAsMemberFails(_aliceId, consume));

        // The matching handle is still accepted, and the earmark is untouched by the refused attempts.
        Assert.Equal("earmarked", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{earmarked}'"));
        await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), earmarked);
        Assert.Equal("held", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{earmarked}'"));
    }

    [Fact]
    public async Task Claim_timestamps_come_from_the_database_clock()
    {
        var handle = OrgClient.NewHandle("clock");
        await Operator($"INSERT INTO gozleme.handle_claims (id, handle, status, created_at) VALUES (gen_random_uuid(), '{handle}', 'blocked', now() - interval '10 days')");
        await Operator($"UPDATE gozleme.handle_claims SET status = 'released', ended_at = now() - interval '10 days' WHERE handle = '{handle}' AND status = 'blocked'");
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{handle}' AND (created_at < now() - interval '1 hour' OR ended_at < now() - interval '1 hour')"));
    }

    private static string Context(Guid account) => GozlemeDatabase.Establish(GozlemeDatabase.SignContext(account));

    private Task Operator(string sql) => GozlemeDatabase.ExecuteAsync(sql, Db.MigratorConnectionString);

    private async Task<string> OperatorFails(string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => Db.AsOwnerAsync<object>(sql))).SqlState;

    private async Task<string> OwnerAsMemberFails(Guid account, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(Db.MigratorConnectionString, Context(account), sql))).SqlState;

    private async Task<long> Rows(Guid account, string sql) =>
        await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, Context(account), $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c");

    private async Task<long> Count(Guid account, string sql) =>
        await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, Context(account), sql);

    private async Task<string> Fails(Guid account, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(Db.AppConnectionString, Context(account), sql))).SqlState;
}
