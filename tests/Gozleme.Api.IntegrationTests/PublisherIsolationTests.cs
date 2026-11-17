using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 US2/US3 in the database with the application skipped (research R2, R5): identifiers, handles, names and
/// owning organizations never change, nothing is ever deleted, aliases are reserved only by the capability's own
/// insert, and the role rows decide every write — for <c>gozleme_app</c> and for the owner's login alike. Every
/// statement runs in a transaction that is rolled back.
/// </summary>
public sealed class PublisherIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Refused = PostgresErrorCodes.InsufficientPrivilege;

    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _carol = null!;
    private HttpClient _erin = null!;
    private HttpClient _bob = null!;
    private Guid _aliceId;
    private Guid _carolId;
    private Guid _erinId;
    private Guid _bobId;
    private Guid _orgA; // alice admin; carol member+publisher; erin member
    private Guid _orgB; // bob admin
    private Guid _publisher;
    private Guid _capability;
    private string _handle = null!;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_erin, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = Guid.Parse(await OrgClient.CreateAsync(_alice, "Isolation A"));
        _orgB = Guid.Parse(await OrgClient.CreateAsync(_bob, "Isolation B"));
        (_carol, _carolId) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, _orgA);
        await OrgClient.JoinAsync(_alice, _erin, _orgA.ToString());
        _aliceId = Guid.Parse(await OrgClient.UserIdAsync(_alice));
        _erinId = Guid.Parse(await OrgClient.UserIdAsync(_erin));
        _bobId = Guid.Parse(await OrgClient.UserIdAsync(_bob));
        _handle = OrgClient.NewHandle("iso");
        _publisher = Guid.Parse((await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), _handle)).GetProperty("id").GetString()!);
        _capability = Guid.Parse((await OrgClient.CreateCapabilityAsync(_carol, _orgA.ToString(), _publisher.ToString(), "lookup")).GetProperty("id").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _alice, _carol, _erin, _bob })
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    public static TheoryData<string> ImmutableChanges =>
    [
        "UPDATE gozleme.publishers SET handle = 'changed-handle' WHERE id = '{publisher}'",
        "UPDATE gozleme.publishers SET id = gen_random_uuid() WHERE id = '{publisher}'",
        "UPDATE gozleme.publishers SET organization_id = '{orgB}' WHERE id = '{publisher}'",
        "UPDATE gozleme.publishers SET created_at = now() - interval '1 day' WHERE id = '{publisher}'",
        "UPDATE gozleme.capabilities SET name = 'changed' WHERE id = '{capability}'",
        "UPDATE gozleme.capabilities SET id = gen_random_uuid() WHERE id = '{capability}'",
        "UPDATE gozleme.capabilities SET organization_id = '{orgB}' WHERE id = '{capability}'",
        "UPDATE gozleme.capabilities SET publisher_id = gen_random_uuid() WHERE id = '{capability}'",
        "UPDATE gozleme.capabilities SET created_at = now() - interval '1 day' WHERE id = '{capability}'",
        "DELETE FROM gozleme.publishers WHERE id = '{publisher}'",
        "DELETE FROM gozleme.capabilities WHERE id = '{capability}'",
        "DELETE FROM gozleme.handle_claims WHERE publisher_id = '{publisher}'",
        "DELETE FROM gozleme.alias_reservations WHERE capability_id = '{capability}'",
        "TRUNCATE gozleme.publishers CASCADE",
        "TRUNCATE gozleme.capabilities CASCADE",
        "TRUNCATE gozleme.handle_claims",
        "TRUNCATE gozleme.alias_reservations",
        "UPDATE gozleme.handle_claims SET handle = 'changed-handle' WHERE publisher_id = '{publisher}'",
        "UPDATE gozleme.alias_reservations SET alias = 'x/y' WHERE capability_id = '{capability}'",
        "INSERT INTO gozleme.alias_reservations (alias, capability_id, organization_id) VALUES ('{handle}/forged', gen_random_uuid(), '{orgA}')",
    ];

    [Theory]
    [MemberData(nameof(ImmutableChanges))]
    public async Task Immutable_columns_deletes_and_forged_aliases_are_refused_for_both_logins(string template)
    {
        var sql = Fill(template);
        var before = await Snapshot();

        // Rows a login cannot see are untouched (0 rows); rows it can see are refused by the guards. The actor the
        // policies admit — the admin on publishers, the publisher member on capabilities — is always refused outright.
        var admitted = sql.StartsWith("UPDATE gozleme.publishers", StringComparison.Ordinal) ? _aliceId
            : sql.StartsWith("UPDATE gozleme.capabilities", StringComparison.Ordinal) ? _carolId
            : (Guid?)null;
        foreach (var connection in new[] { Db.AppConnectionString, Db.MigratorConnectionString })
        {
            foreach (var account in new[] { _aliceId, _carolId, _erinId })
            {
                var outcome = await Attempt(connection, Context(account), sql);
                Assert.True(outcome is Refused || (outcome == "0" && account != admitted), $"{outcome} as {account} on {(connection == Db.AppConnectionString ? "app" : "owner")}: {sql}");
            }
        }

        // The owner's own login with no account context (the operator) is bound too.
        var owner = await Attempt(Db.MigratorConnectionString, null, sql);
        Assert.True(owner is Refused or "0", $"{owner} as the operator: {sql}");
        Assert.Equal(before, await Snapshot());
    }

    /// <summary>Runs one statement in a rolled-back transaction: the SQLSTATE if refused, else the affected row count.</summary>
    private static async Task<string> Attempt(string connectionString, string? setup, string sql)
    {
        try
        {
            return await GozlemeDatabase.WithConnectionAsync(connectionString, async connection =>
            {
                await using var tx = await connection.BeginTransactionAsync();
                if (setup is not null)
                {
                    await Exec(connection, tx, setup);
                }

                await using var command = new NpgsqlCommand(sql, connection, tx);
                var rows = await command.ExecuteNonQueryAsync();
                await tx.RollbackAsync();
                return Math.Max(rows, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    [Fact]
    public async Task The_app_cannot_read_alias_reservations()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, Context(_carolId), "SELECT count(*) FROM gozleme.alias_reservations"));
        Assert.Equal(Refused, ex.SqlState);
    }

    [Fact]
    public async Task Role_rows_decide_every_write_and_reads_are_member_level()
    {
        foreach (var connection in new[] { Db.AppConnectionString, Db.MigratorConnectionString })
        {
            // Only an admin inserts a publisher; only a publisher inserts a capability; admin alone is not enough.
            Assert.Equal(Refused, await Fails(connection, _carolId, InsertPublisher(_carolId)));
            Assert.Equal(Refused, await Fails(connection, _erinId, InsertPublisher(_erinId)));
            Assert.Equal(Refused, await Fails(connection, _aliceId, InsertCapability(_aliceId, "by-admin")));
            Assert.Equal(Refused, await Fails(connection, _erinId, InsertCapability(_erinId, "by-member")));
            Assert.Equal(Refused, await Fails(connection, _bobId, InsertCapability(_bobId, "by-outsider")));

            // Renames by the wrong role touch nothing.
            Assert.Equal(0, await Rows(connection, _erinId, $"UPDATE gozleme.capabilities SET display_name = 'X' WHERE id = '{_capability}'"));
            Assert.Equal(0, await Rows(connection, _carolId, $"UPDATE gozleme.publishers SET display_name = 'X' WHERE id = '{_publisher}'"));
            Assert.Equal(0, await Rows(connection, _aliceId, $"UPDATE gozleme.capabilities SET display_name = 'X' WHERE id = '{_capability}'"));
            Assert.Equal(0, await Rows(connection, _bobId, $"UPDATE gozleme.publishers SET display_name = 'X' WHERE id = '{_publisher}'"));

            // Allowed writes succeed.
            Assert.Equal(1, await Rows(connection, _aliceId, $"UPDATE gozleme.publishers SET display_name = 'Renamed' WHERE id = '{_publisher}'"));
            Assert.Equal(1, await Rows(connection, _carolId, $"UPDATE gozleme.capabilities SET display_name = 'Renamed' WHERE id = '{_capability}'"));
            Assert.Equal(1, await Rows(connection, _carolId, InsertCapability(_carolId, "allowed")));

            // Reads: every member sees both; a non-member sees nothing (FR-024).
            foreach (var member in new[] { _aliceId, _carolId, _erinId })
            {
                Assert.Equal(1, await Count(connection, member, $"SELECT count(*) FROM gozleme.publishers WHERE id = '{_publisher}'"));
                Assert.Equal(1, await Count(connection, member, $"SELECT count(*) FROM gozleme.capabilities WHERE id = '{_capability}'"));
            }

            Assert.Equal(0, await Count(connection, _bobId, $"SELECT count(*) FROM gozleme.publishers WHERE organization_id = '{_orgA}'"));
            Assert.Equal(0, await Count(connection, _bobId, $"SELECT count(*) FROM gozleme.capabilities WHERE organization_id = '{_orgA}'"));
        }

        // The application's admin may register (publisher + held claim in one transaction).
        var registered = await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, Context(_aliceId), $"""
            WITH p AS ({InsertPublisher(_aliceId, "fresh-" + Guid.NewGuid().ToString("N")[..8])} RETURNING id, handle, organization_id)
            SELECT count(*) FROM p
            """);
        Assert.Equal(1, registered);
    }

    [Fact]
    public async Task A_role_lost_during_a_request_refuses_its_next_statement_but_not_what_it_already_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var kept = Guid.CreateVersion7();
        await GozlemeDatabase.WithConnectionAsync(Db.AppConnectionString, async connection =>
        {
            await using (var tx = await connection.BeginTransactionAsync(ct)) // READ COMMITTED, like TenantTransaction
            {
                await Exec(connection, tx, Context(_carolId));
                await Exec(connection, tx, InsertCapability(_carolId, "kept-before", kept));
                await tx.CommitAsync(ct);
            }

            await using var open = await connection.BeginTransactionAsync(ct);
            await Exec(connection, open, Context(_carolId));
            await OrgClient.SetRolesAsync(_alice, _orgA.ToString(), _carolId.ToString()); // committed on another connection
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(connection, open, InsertCapability(_carolId, "after-revoke")));
            Assert.Equal(Refused, ex.SqlState);
            await open.RollbackAsync(ct);
            return 0;
        });

        Assert.Equal("kept-before", await Db.ScalarAsync<string>($"SELECT name FROM gozleme.capabilities WHERE id = '{kept}'"));
        Assert.Equal($"{_handle}/kept-before", await Db.ScalarAsync<string>($"SELECT alias FROM gozleme.alias_reservations WHERE capability_id = '{kept}'"));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.capabilities WHERE name = 'after-revoke'"));
    }

    private string Fill(string template) => template
        .Replace("{publisher}", _publisher.ToString(), StringComparison.Ordinal)
        .Replace("{capability}", _capability.ToString(), StringComparison.Ordinal)
        .Replace("{orgA}", _orgA.ToString(), StringComparison.Ordinal)
        .Replace("{orgB}", _orgB.ToString(), StringComparison.Ordinal)
        .Replace("{handle}", _handle, StringComparison.Ordinal);

    private string InsertPublisher(Guid account, string? handle = null) =>
        $"INSERT INTO gozleme.publishers (id, organization_id, handle, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), '{_orgA}', '{handle ?? "p-" + Guid.NewGuid().ToString("N")[..8]}', 'X', now(), '{account}', now())";

    private string InsertCapability(Guid account, string name, Guid? id = null) =>
        $"INSERT INTO gozleme.capabilities (id, organization_id, publisher_id, name, display_name, created_at, created_by, updated_at) VALUES ('{id ?? Guid.CreateVersion7()}', '{_orgA}', '{_publisher}', '{name}', 'X', now(), '{account}', now())";

    private static string Context(Guid account) => GozlemeDatabase.Establish(GozlemeDatabase.SignContext(account));

    // Scoped to this class's organizations: the database is shared with other classes that write concurrently.
    private Task<string> Snapshot() => Db.ScalarAsync<string>($"""
        SELECT concat_ws('|',
          (SELECT string_agg(concat_ws(',', id, organization_id, handle, created_at), ';' ORDER BY id) FROM gozleme.publishers WHERE organization_id IN ('{_orgA}', '{_orgB}')),
          (SELECT string_agg(concat_ws(',', id, organization_id, publisher_id, name, created_at), ';' ORDER BY id) FROM gozleme.capabilities WHERE organization_id IN ('{_orgA}', '{_orgB}')),
          (SELECT string_agg(concat_ws(',', id, handle, status, publisher_id), ';' ORDER BY id) FROM gozleme.handle_claims WHERE organization_id IN ('{_orgA}', '{_orgB}') OR handle = '{_handle}'),
          (SELECT string_agg(concat_ws(',', alias, capability_id), ';' ORDER BY alias) FROM gozleme.alias_reservations WHERE organization_id IN ('{_orgA}', '{_orgB}')))
        """);

    private static async Task<long> Rows(string connection, Guid account, string sql) =>
        await GozlemeDatabase.InTransactionAsync<long>(connection, Context(account), $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c");

    private static async Task<long> Count(string connection, Guid account, string sql) =>
        await GozlemeDatabase.InTransactionAsync<long>(connection, Context(account), sql);

    private static async Task<string> Fails(string connection, Guid account, string sql) =>
        (await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(connection, Context(account), sql))).SqlState;

    private static async Task Exec(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
