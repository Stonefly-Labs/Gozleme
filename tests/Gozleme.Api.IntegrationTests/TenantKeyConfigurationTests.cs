using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Persistence;
using Gozleme.Api.Tenancy;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 FR-012 and the "key missing or too short" edge case: the API refuses to start, and the
/// migrator exits 1 without touching the provisioned key. Neither falls back to an unsigned mode, and
/// the refusal never echoes the configured value.
/// </summary>
public sealed class TenantKeyConfigurationTests(PgvectorDatabaseFixture fixture)
{
    private const string ShortKey = "short-key-not-a-secret-0003";

    private GozlemeDatabase Db => fixture.Database;

    [Theory]
    [InlineData("")]
    [InlineData(ShortKey)]
    public async Task The_api_refuses_to_start_without_a_key_of_adequate_length(string key)
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Rls:ContextKey"] = key });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var validation = ex as OptionsValidationException ?? ex.InnerException as OptionsValidationException;
        Assert.NotNull(validation);
        Assert.Contains($"{TenancyOptions.Section}:ContextKey", validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ShortKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ShortKey)]
    public async Task The_migrator_exits_1_and_leaves_the_provisioned_key_unchanged(string? key)
    {
        var before = await Db.ScalarAsync<string>("SELECT encode(sha256(key), 'hex') FROM gozleme_private.context_keys");
        string[] args = key is null
            ? [$"--ConnectionStrings:{MigrationRunner.MigratorConnectionStringName}={Db.MigratorConnectionString}"]
            : [$"--ConnectionStrings:{MigrationRunner.MigratorConnectionStringName}={Db.MigratorConnectionString}", $"--{TenancyOptions.Section}:ContextKey={key}"];

        Assert.Equal(1, await MigrationRunner.RunAsync(args));
        Assert.Equal(before, await Db.ScalarAsync<string>("SELECT encode(sha256(key), 'hex') FROM gozleme_private.context_keys"));
    }
}
