using Npgsql;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Two organizations with disjoint members, built through the real API (GOZ-0003): A = alice and carol
/// (both admins since GOZ-0004, so carol may invite) with one open invitation created by carol; B = bob
/// (admin) with one open invitation. Fresh accounts every
/// time, so tests on the shared database never see each other's rows.
/// </summary>
public sealed record TwoTenants(
    HttpClient Alice, Guid AliceId, HttpClient Carol, Guid CarolId, Guid OrgA, string CodeA,
    HttpClient Bob, Guid BobId, Guid OrgB, string CodeB) : IDisposable
{
    public static async Task<TwoTenants> CreateAsync(GozlemeApiFactory factory)
    {
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        var (carol, _) = await AuthClient.SignedInAsync(factory);
        var (bob, _) = await AuthClient.SignedInAsync(factory);
        var orgA = await OrgClient.CreateAsync(alice, "Tenant A");
        await OrgClient.JoinAsync(alice, carol, orgA);
        await OrgClient.SetRolesAsync(alice, orgA, await OrgClient.UserIdAsync(carol), "admin");
        var (_, codeA) = await OrgClient.InviteAsync(carol, orgA);
        var orgB = await OrgClient.CreateAsync(bob, "Tenant B");
        var (_, codeB) = await OrgClient.InviteAsync(bob, orgB);
        return new TwoTenants(
            alice, Guid.Parse(await OrgClient.UserIdAsync(alice)), carol, Guid.Parse(await OrgClient.UserIdAsync(carol)), Guid.Parse(orgA), codeA,
            bob, Guid.Parse(await OrgClient.UserIdAsync(bob)), Guid.Parse(orgB), codeB);
    }

    /// <summary>Rows of <paramref name="table"/> for <paramref name="org"/> as the superuser sees them (ground truth, bypasses RLS).</summary>
    public static Task<long> TruthAsync(GozlemeDatabase db, string table, Guid org) =>
        db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.{table} WHERE {(table == "organizations" ? "id" : "organization_id")} = '{org}'");

    public static bool IsRefusal(PostgresException ex) => ex.SqlState == PostgresErrorCodes.InsufficientPrivilege;

    public void Dispose()
    {
        Alice.Dispose();
        Carol.Dispose();
        Bob.Dispose();
    }
}
