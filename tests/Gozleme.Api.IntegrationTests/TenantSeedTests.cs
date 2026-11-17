using System.Net;
using Gozleme.Api.Identity;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 US6 (FR-024, FR-025; maintainer decision: seed acts as a seeded member). Two disjoint demo
/// tenants; seed never reaches an unrelated organization; an organization no seeded member belongs to
/// cannot be restored; identity operator commands need no tenant context. Own database, because these
/// tests break the fixed demo organizations on purpose.
/// </summary>
public sealed class TenantSeedTests : IAsyncLifetime
{
    private const string DemoPassword = "it-demo-seed-passphrase";

    private GozlemeDatabase _db = null!;
    private GozlemeApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _factory = new GozlemeApiFactory(_db.AppConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Seed_creates_two_disjoint_demo_tenants_and_leaves_other_organizations_alone()
    {
        var (stranger, _) = await AuthClient.SignedInAsync(_factory);
        using var _s = stranger;
        var unrelated = await OrgClient.CreateAsync(stranger, "Unrelated");
        await OrgClient.InviteAsync(stranger, unrelated);
        var before = await Snapshot(unrelated);

        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);
        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);

        Assert.Equal("alice@demo.gozleme.test,carol@demo.gozleme.test", await Members(OperatorCommands.DemoOrganizationId));
        Assert.Equal("dave@demo.gozleme.test", await Members(OperatorCommands.PartnerOrganizationId));
        Assert.Equal(OperatorCommands.PartnerOrganizationName, await _db.ScalarAsync<string>($"SELECT display_name FROM gozleme.organizations WHERE id = '{OperatorCommands.PartnerOrganizationId}'"));
        Assert.Equal(before, await Snapshot(unrelated));

        using var dave = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(dave, "dave@demo.gozleme.test", DemoPassword)).StatusCode);
        Assert.Equal([OperatorCommands.PartnerOrganizationId.ToString()], await OrgClient.IdsAsync(dave));
        using var other = await AuthClient.GetAsync(dave, $"{OrgClient.Base}/{OperatorCommands.DemoOrganizationId}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Seed_restores_a_drifted_tenant_acting_as_whichever_seeded_admin_remains()
    {
        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);
        Assert.Equal("alice@demo.gozleme.test:admin,member;carol@demo.gozleme.test:member,publisher", await MemberRoles(OperatorCommands.DemoOrganizationId));
        Assert.Equal("dave@demo.gozleme.test:admin,member", await MemberRoles(OperatorCommands.PartnerOrganizationId));

        var demo = OperatorCommands.DemoOrganizationId;
        var (alice, bob) = (await UserId("alice"), await UserId("bob"));
        await Drift($"""
            DELETE FROM gozleme.memberships WHERE organization_id = '{demo}' AND user_id = '{alice}';
            INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via, roles) VALUES ('{demo}', '{bob}', now(), 'invitation', {AdminMember});
            UPDATE gozleme.memberships SET roles = {AdminMember} WHERE organization_id = '{demo}' AND user_id = '{await UserId("carol")}';
            UPDATE gozleme.organizations SET display_name = 'Drifted' WHERE id = '{demo}';
            """);

        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code); // only carol is left to act as, and she holds admin
        Assert.Equal("alice@demo.gozleme.test:admin,member;carol@demo.gozleme.test:member,publisher", await MemberRoles(demo));
        Assert.Equal(OperatorCommands.DemoOrganizationName, await _db.ScalarAsync<string>($"SELECT display_name FROM gozleme.organizations WHERE id = '{demo}'"));
    }

    [Fact]
    public async Task A_tenant_whose_remaining_seeded_members_are_not_admins_cannot_be_restored()
    {
        // GOZ-0004 research R11: seed acts only as a seeded admin, so an ordinary member's rights cannot restore it.
        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);
        var demo = OperatorCommands.DemoOrganizationId;
        var (alice, bob) = (await UserId("alice"), await UserId("bob"));
        await Drift($"""
            DELETE FROM gozleme.memberships WHERE organization_id = '{demo}' AND user_id = '{alice}';
            INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via, roles) VALUES ('{demo}', '{bob}', now(), 'invitation', {AdminMember});
            """);

        var (code, _, stderr) = await SeedAsync();
        Assert.Equal(OperatorCommands.Failed, code);
        Assert.Contains(OperatorCommands.UnrestorableMessage, stderr, StringComparison.Ordinal);
        Assert.Equal("bob@demo.gozleme.test:admin,member;carol@demo.gozleme.test:member,publisher", await MemberRoles(demo));
    }

    [Fact]
    public async Task An_organization_no_seeded_member_belongs_to_cannot_be_restored_with_a_members_rights()
    {
        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);
        var partner = OperatorCommands.PartnerOrganizationId;
        var bob = await _db.ScalarAsync<Guid>("SELECT id FROM gozleme.users WHERE email = 'bob@demo.gozleme.test'");
        await Drift($"""
            INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via, roles) VALUES ('{partner}', '{bob}', now(), 'invitation', {AdminMember});
            DELETE FROM gozleme.memberships WHERE organization_id = '{partner}' AND user_id <> '{bob}';
            """);

        var (code, _, stderr) = await SeedAsync();
        Assert.Equal(OperatorCommands.Failed, code);
        Assert.Contains(OperatorCommands.UnrestorableMessage, stderr, StringComparison.Ordinal);
        Assert.Equal("bob@demo.gozleme.test", await Members(partner));
    }

    [Fact]
    public async Task Seed_requires_the_context_key_but_identity_commands_do_not()
    {
        Assert.Equal(OperatorCommands.Failed, (await RunAsync("seed", [$"--{OperatorCommands.DemoPasswordKey}={DemoPassword}"], withKey: false)).Code);
        Assert.Equal(OperatorCommands.Ok, (await SeedAsync()).Code);
        Assert.Equal(OperatorCommands.Ok, (await RunAsync("unlock", ["--email", "lockout@demo.gozleme.test"], withKey: false)).Code);
        Assert.Equal(OperatorCommands.Ok, (await RunAsync("reset-password", ["--email", "bob@demo.gozleme.test", "--password-stdin"], withKey: false, stdin: "a-fresh-operator-passphrase")).Code);
    }

    private const string AdminMember = "'{admin,member}'";

    /// <summary>Out-of-band drift as the superuser; replica mode skips memberships_guard, which binds superusers too (GOZ-0004).</summary>
    private Task Drift(string sql) => GozlemeDatabase.ExecuteAsync("SET session_replication_role = replica;\n" + sql, _db.SuperuserConnectionString);

    private Task<Guid> UserId(string name) => _db.ScalarAsync<Guid>($"SELECT id FROM gozleme.users WHERE email = '{name}@demo.gozleme.test'");

    private Task<string> MemberRoles(Guid organizationId) => _db.ScalarAsync<string>($"""
        SELECT string_agg(u.email || ':' || array_to_string(m.roles, ','), ';' ORDER BY u.email)
        FROM gozleme.memberships m JOIN gozleme.users u ON u.id = m.user_id WHERE m.organization_id = '{organizationId}'
        """);

    private Task<(int Code, string Stdout, string Stderr)> SeedAsync() =>
        RunAsync("seed", [$"--{OperatorCommands.DemoPasswordKey}={DemoPassword}"], withKey: true);

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string command, string[] args, bool withKey, string stdin = "")
    {
        using var input = new StringReader(stdin);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        string[] settings = withKey
            ? [$"--ConnectionStrings:Gozleme={_db.AppConnectionString}", $"--Gozleme:Rls:ContextKey={GozlemeDatabase.ContextKey}"]
            : [$"--ConnectionStrings:Gozleme={_db.AppConnectionString}"];
        var code = await OperatorCommands.RunAsync(command, [.. args, .. settings], input, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private Task<string> Members(Guid organizationId) => _db.ScalarAsync<string>($"""
        SELECT string_agg(u.email, ',' ORDER BY u.email) FROM gozleme.memberships m JOIN gozleme.users u ON u.id = m.user_id
        WHERE m.organization_id = '{organizationId}'
        """);

    private Task<string> Snapshot(string organizationId) => _db.ScalarAsync<string>($"""
        SELECT (SELECT display_name || '|' || updated_at FROM gozleme.organizations WHERE id = '{organizationId}')
          || '|' || (SELECT string_agg(user_id::text, ',' ORDER BY user_id) FROM gozleme.memberships WHERE organization_id = '{organizationId}')
          || '|' || (SELECT string_agg(id::text || coalesce(revoked_at::text, ''), ',' ORDER BY id) FROM gozleme.invitations WHERE organization_id = '{organizationId}')
        """);
}
