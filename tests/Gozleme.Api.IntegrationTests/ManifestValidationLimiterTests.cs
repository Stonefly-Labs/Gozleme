using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Manifests;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 FR-020 (maintainer decision 2026-11-18, "shared-rate-limit", T099): the manifest validation budget lives in
/// PostgreSQL, so separate API replicas share one count per account and a restart does not reset it. Each limiter here
/// has its own data source, standing in for one API process.
/// </summary>
public sealed class ManifestValidationLimiterTests(PgvectorDatabaseFixture fixture)
{
    private GozlemeDatabase Db => fixture.Database;

    [Fact]
    public async Task Replicas_share_one_budget_per_account_and_a_restart_keeps_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (account, other) = (await NewAccountAsync(), await NewAccountAsync());
        var options = Options.Create(new ManifestOptions { ValidatePerMinute = 3 });
        await using (var first = Named())
        await using (var second = Named())
        {
            var replicaA = new ManifestValidationLimiter(first, options);
            var replicaB = new ManifestValidationLimiter(second, options);
            Assert.Null(await replicaA.TryAcquireAsync(account, ct));
            Assert.Null(await replicaB.TryAcquireAsync(account, ct));
            Assert.Null(await replicaA.TryAcquireAsync(account, ct));
            var retryAfter = await replicaB.TryAcquireAsync(account, ct);
            Assert.InRange(retryAfter!.Value, 1, 60);
            Assert.Null(await replicaB.TryAcquireAsync(other, ct)); // another account's budget is its own
        }

        // A restarted process (a new data source and limiter) still sees the spent budget.
        await using var restarted = Named();
        Assert.NotNull(await new ManifestValidationLimiter(restarted, options).TryAcquireAsync(account, ct));
        Assert.Equal(3, await Db.ScalarAsync<int>($"SELECT attempts FROM gozleme.manifest_validation_attempts WHERE account_id = '{account}'"));
    }

    [Fact]
    public async Task Concurrent_attempts_never_exceed_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await NewAccountAsync();
        var options = Options.Create(new ManifestOptions { ValidatePerMinute = 5 });
        await using var dataSource = Named();
        var limiters = Enumerable.Range(0, 4).Select(_ => new ManifestValidationLimiter(dataSource, options)).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => limiters[i % 4].TryAcquireAsync(account, ct)));
        Assert.Equal(5, results.Count(r => r is null));
    }

    [Fact]
    public async Task Expired_windows_are_dropped_and_account_deletion_removes_the_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await NewAccountAsync();
        await GozlemeDatabase.ExecuteAsync(
            $"INSERT INTO gozleme.manifest_validation_attempts (account_id, window_start, attempts) VALUES ('{account}', date_trunc('minute', now()) - interval '2 minutes', 99)",
            Db.SuperuserConnectionString);
        await using var dataSource = Named();
        Assert.Null(await new ManifestValidationLimiter(dataSource, Options.Create(new ManifestOptions { ValidatePerMinute = 1 })).TryAcquireAsync(account, ct));
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_validation_attempts WHERE account_id = '{account}'"));

        await GozlemeDatabase.ExecuteAsync($"DELETE FROM gozleme.users WHERE id = '{account}'", Db.SuperuserConnectionString);
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_validation_attempts WHERE account_id = '{account}'"));
    }

    private NpgsqlDataSource Named() => new NpgsqlDataSourceBuilder(Db.AppConnectionString) { Name = "it-manifest-limiter" }.Build();

    private async Task<Guid> NewAccountAsync()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString);
        var (client, _) = await AuthClient.SignedInAsync(factory);
        using var _c = client;
        return Guid.Parse(await OrgClient.UserIdAsync(client));
    }
}
