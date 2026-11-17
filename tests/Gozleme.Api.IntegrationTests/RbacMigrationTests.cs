using System.Net;
using System.Text.RegularExpressions;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0004 US4 (FR-014, FR-015, SC-005; maintainer Option B): the upgrade is DDL-only, every existing
/// member keeps managing its organization as <c>{admin,member}</c>, and new joiners get least privilege.
/// Runs on its own database because rolling back the migration would disturb the shared fixture.
/// </summary>
public sealed partial class RbacMigrationTests : IAsyncLifetime
{
    private const string Previous = "20261117000000_TenantRls";
    private const string ScopedRbac = "20261118000000_ScopedRbac";

    private GozlemeDatabase _db = null!;
    private GozlemeApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync(GozlemeDatabase.PlainImage);
        _factory = new GozlemeApiFactory(_db.AppConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Existing_members_become_admins_and_keep_managing_their_organization()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        var (bob, _) = await AuthClient.SignedInAsync(_factory);
        var (carol, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        using var _c = carol;
        var org = await OrgClient.CreateAsync(alice, "Before upgrade");
        await OrgClient.JoinAsync(alice, bob, org);

        await MigrateToAsync(Previous); // Down: back to the GOZ-0003 schema, data kept
        Assert.Equal(0, await _db.ScalarAsync<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'gozleme' AND column_name = 'roles'"));
        await MigrateToAsync(ScopedRbac); // Up again: the real upgrade path

        Assert.Equal(2, await _db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{org}' AND roles = '{{admin,member}}'"));
        using (var renamed = await OrgClient.RenameAsync(bob, org, "After upgrade"))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }

        await OrgClient.JoinAsync(bob, carol, org); // bob may invite
        var (members, carolId) = (await OrgClient.MembersAsync(bob, org), await OrgClient.UserIdAsync(carol));
        Assert.Equal(["member"], members.Single(m => m.GetProperty("userId").GetString() == carolId).GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        using (var removed = await OrgClient.RemoveAsync(bob, org, await OrgClient.UserIdAsync(alice)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var created = await OrgClient.CreateAsync(carol, "New after upgrade");
        Assert.Equal("{admin,member}", await _db.ScalarAsync<string>($"SELECT roles::text FROM gozleme.memberships WHERE organization_id = '{created}'"));
    }

    [Fact]
    public async Task The_upgrade_is_ddl_only_and_never_relaxes_row_security()
    {
        await using var context = Context();
        var script = context.GetService<IMigrator>().GenerateScript(Previous, ScopedRbac);
        Assert.DoesNotMatch(TenantDml(), script);
        Assert.DoesNotContain("rls_helper", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NO FORCE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECURITY DEFINER", script, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MigrateToAsync(string migration)
    {
        await using var context = Context();
        await context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);
    }

    private GozlemeDbContext Context()
    {
        // Named, so Npgsql metrics label the pool by name and never by connection string.
        var dataSource = new NpgsqlDataSourceBuilder(_db.MigratorConnectionString) { Name = "integration-test-fixture" }.Build();
        return new GozlemeDbContext(GozlemeDbContext.Configure(new DbContextOptionsBuilder<GozlemeDbContext>(), dataSource).Options as DbContextOptions<GozlemeDbContext>
            ?? throw new InvalidOperationException());
    }

    [GeneratedRegex(@"(UPDATE|INSERT\s+INTO|DELETE\s+FROM)\s+gozleme\.(organizations|memberships|invitations)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TenantDml();
}
