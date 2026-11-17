using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Publishers;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 US2 registry origin (FR-002, FR-003, research R1, R6): recorded once by the migrator, compared before any
/// migration is applied, never overwritten; the API refuses to start on a mismatch, starts with the database down and
/// answers <c>503</c> on publisher endpoints until it has verified the origin. Each test owns a fresh database.
/// </summary>
public sealed class RegistryOriginTests : IAsyncLifetime
{
    private GozlemeDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await GozlemeDatabase.StartAsync(migrate: false);

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Registry.Example.com")]
    [InlineData("https://registry.example.com")]
    [InlineData("registry.example.com:443")]
    public async Task A_missing_or_invalid_origin_applies_no_migration(string? origin)
    {
        Assert.Equal(1, await _db.MigrateAsync(origin));
        Assert.Equal("none", await _db.ScalarAsync<string>("SELECT coalesce(to_regclass('public.\"__EFMigrationsHistory\"')::text, 'none')"));
        Assert.Equal("none", await _db.ScalarAsync<string>("SELECT coalesce(to_regclass('gozleme.publishers')::text, 'none')"));
    }

    [Fact]
    public async Task The_origin_is_recorded_once_and_a_different_one_is_refused_before_migrating()
    {
        Assert.Equal(0, await _db.MigrateAsync());
        Assert.Equal(GozlemeDatabase.Origin, await _db.ScalarAsync<string>("SELECT origin FROM gozleme.registry_origin"));
        var applied = await _db.ScalarAsync<long>("SELECT count(*) FROM public.\"__EFMigrationsHistory\"");

        Assert.Equal(0, await _db.MigrateAsync()); // the same value: a no-op
        Assert.Equal(1, await _db.ScalarAsync<long>("SELECT count(*) FROM gozleme.registry_origin"));

        Assert.Equal(1, await _db.MigrateAsync("other.test.gozleme.test"));
        Assert.Equal(1, await _db.MigrateAsync(null));
        Assert.Equal(GozlemeDatabase.Origin, await _db.ScalarAsync<string>("SELECT origin FROM gozleme.registry_origin"));
        Assert.Equal(applied, await _db.ScalarAsync<long>("SELECT count(*) FROM public.\"__EFMigrationsHistory\""));

        // Not even the owner can change or remove it.
        foreach (var sql in new[] { "UPDATE gozleme.registry_origin SET origin = 'other.test'", "DELETE FROM gozleme.registry_origin", "TRUNCATE gozleme.registry_origin" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.AsOwnerAsync<object>(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        var app = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(_db.AppConnectionString, null, "UPDATE gozleme.registry_origin SET origin = 'other.test'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, app.SqlState);
    }

    [Fact]
    public async Task The_api_refuses_to_start_with_a_different_origin()
    {
        Assert.Equal(0, await _db.MigrateAsync());
        await using var factory = new GozlemeApiFactory(_db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Registry:Origin"] = "other.test.gozleme.test" });
        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        Assert.Contains(RegistryOrigin.MismatchMessage, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_starts_with_the_database_down_and_verifies_the_origin_once_it_is_back()
    {
        Assert.Equal(0, await _db.MigrateAsync());
        var ct = TestContext.Current.CancellationToken;
        await _db.StopAsync();
        await using var factory = new GozlemeApiFactory(_db.AppConnectionString);
        using var anonymous = factory.CreateClient(); // starts: verification deferred
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live", ct)).StatusCode);
        Assert.Contains(factory.Logs, l => l.Contains("registry origin verification deferred", StringComparison.Ordinal));

        await _db.StartAgainAsync();
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Origin later");
        var publisher = await OrgClient.RegisterPublisherAsync(alice, org);
        Assert.StartsWith($"gz://{GozlemeDatabase.Origin}/pub/", publisher.GetProperty("publisherId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publisher_endpoints_answer_503_while_the_origin_is_unverified()
    {
        Assert.Equal(0, await _db.MigrateAsync());
        // Only a superuser with the guard disabled can produce an unrecorded origin on a migrated database.
        await GozlemeDatabase.ExecuteAsync(
            "ALTER TABLE gozleme.registry_origin DISABLE TRIGGER registry_origin_guard; DELETE FROM gozleme.registry_origin; ALTER TABLE gozleme.registry_origin ENABLE TRIGGER registry_origin_guard;",
            _db.SuperuserConnectionString);
        await using var factory = new GozlemeApiFactory(_db.AppConnectionString);
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "Origin unverified");
        Assert.Equal(
            (HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}""", true),
            await OrgClient.SnapshotAsync(alice, HttpMethod.Get, OrgClient.Publishers(org), null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await OrgClient.SnapshotAsync(alice, HttpMethod.Post, OrgClient.Publishers(org), new { handle = OrgClient.NewHandle(), displayName = "X" })).Status);
        Assert.Equal(0, await _db.ScalarAsync<long>("SELECT count(*) FROM gozleme.publishers"));

        await GozlemeDatabase.ExecuteAsync($"INSERT INTO gozleme.registry_origin (id, origin) VALUES (1, '{GozlemeDatabase.Origin}')", _db.SuperuserConnectionString);
        await OrgClient.RegisterPublisherAsync(alice, org);
    }
}
