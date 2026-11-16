using Gozleme.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Tenancy;

/// <summary>
/// Database side of the account context (contracts/tenant-context.md §2). Every call must run inside
/// an explicit transaction: the settings are transaction-local, so they vanish at commit or rollback
/// and never survive on a pooled connection (FR-009).
/// </summary>
internal static class TenantContext
{
    /// <summary>Stores a signed token for this transaction; returns <see cref="ContextResult"/> ok, missing, invalid or expired.</summary>
    public static async Task<string> EstablishAsync(GozlemeDbContext db, string? token, CancellationToken ct) =>
        await db.Database.SqlQuery<string>($"SELECT gozleme.establish_context({token ?? string.Empty}) AS \"Value\"").SingleAsync(ct);

    /// <summary>Presents an invitation code hash as a bearer capability for that one invitation (research R6).</summary>
    public static Task PresentInvitationAsync(GozlemeDbContext db, byte[] codeHash, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT set_config('gozleme.invitation_capability', {Convert.ToHexStringLower(codeHash)}, true)", ct);

    /// <summary>Locks the invited organization <c>FOR KEY SHARE</c> (lock order organization, then invitation) and returns its id.</summary>
    public static async Task<Guid?> LockInvitedOrganizationAsync(GozlemeDbContext db, byte[] codeHash, CancellationToken ct) =>
        await db.Database.SqlQuery<Guid?>($"SELECT gozleme.lock_invited_organization({codeHash}) AS \"Value\"").SingleAsync(ct);

    /// <summary>A row-level security refusal (<c>42501</c>), raised directly or wrapped by <see cref="DbUpdateException"/>.</summary>
    public static bool IsRefusal(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.InsufficientPrivilege };
}
