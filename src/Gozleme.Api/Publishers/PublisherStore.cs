using Gozleme.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Publishers;

/// <summary>Outcome of a store write; the endpoint maps it to a response (research R8).</summary>
internal enum WriteResult
{
    Ok,
    HandleUnavailable,
    Conflict,
    NotFound,
}

/// <summary>
/// Publisher and capability persistence in the request's tenant transaction (data-model.md). Claims are reached
/// through narrow parameterized SQL only, and an alias never through the application at all: the database derives
/// and reserves it. No lock is taken on <c>organizations</c>, so the GOZ-0004 lock order is unchanged. A constraint
/// error is mapped to the decided answers and every other one to the unknown-ID 404 (GOZ-0003-IF-06); after one,
/// the aborted transaction is rolled back, so <c>TenantTransaction</c> does not commit.
/// </summary>
internal sealed class PublisherStore(GozlemeDbContext db, TimeProvider time)
{
    public const string HandleUniqueIndex = "ux_handle_claims_live";
    public const string CapabilityNameIndex = "ix_capabilities_publisher_id_name";
    public const string AliasPrimaryKey = "alias_reservations_pkey";

    public async Task<List<Publisher>> ListAsync(Guid organizationId, CancellationToken ct) =>
        await db.Publishers.Where(p => p.OrganizationId == organizationId)
            .OrderBy(p => p.Handle).ThenBy(p => p.Id)
            .ToListAsync(ct);

    /// <summary>The publishers of <paramref name="organizationId"/> that still hold their handle claim.</summary>
    public async Task<HashSet<Guid>> ActiveAsync(Guid organizationId, CancellationToken ct) =>
        [.. await db.Database.SqlQuery<Guid>($"""
            SELECT publisher_id AS "Value" FROM gozleme.handle_claims WHERE organization_id = {organizationId} AND status = 'held'
            """).ToListAsync(ct)];

    public async Task<bool> IsActiveAsync(Publisher publisher, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""
            SELECT publisher_id AS "Value" FROM gozleme.handle_claims
            WHERE publisher_id = {publisher.Id} AND organization_id = {publisher.OrganizationId} AND status = 'held'
            """).ToListAsync(ct)).Count > 0;

    /// <summary>A malformed id, another organization's id and an unknown id are all null.</summary>
    public async Task<Publisher?> FindAsync(Guid organizationId, string? publisherId, CancellationToken ct) =>
        Guid.TryParse(publisherId, out var id)
            ? await db.Publishers.FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId, ct)
            : null;

    public async Task<List<Capability>> CapabilitiesAsync(Publisher publisher, CancellationToken ct) =>
        await db.Capabilities.Where(c => c.PublisherId == publisher.Id && c.OrganizationId == publisher.OrganizationId)
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .ToListAsync(ct);

    /// <summary>A capability id under the wrong publisher is null too.</summary>
    public async Task<Capability?> FindCapabilityAsync(Publisher publisher, string? capabilityId, CancellationToken ct) =>
        Guid.TryParse(capabilityId, out var id)
            ? await db.Capabilities.FirstOrDefaultAsync(c => c.Id == id && c.PublisherId == publisher.Id && c.OrganizationId == publisher.OrganizationId, ct)
            : null;

    /// <summary>
    /// Inserts the publisher, then consumes this organization's earmark for the handle or inserts a held claim, in
    /// the request transaction (research R2). The live unique index makes a concurrent registration wait, then fail.
    /// </summary>
    public async Task<(WriteResult Result, Publisher? Publisher, Guid ClaimId)> RegisterAsync(
        Guid organizationId, Guid userId, string handle, string displayName, CancellationToken ct)
    {
        var now = Now();
        var publisher = new Publisher
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Handle = handle,
            DisplayName = displayName,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
        };
        try
        {
            db.Publishers.Add(publisher);
            await db.SaveChangesAsync(ct);
            var consumed = await db.Database.SqlQuery<Guid>($"""
                UPDATE gozleme.handle_claims SET status = 'held', publisher_id = {publisher.Id}, held_at = {now}
                WHERE handle = {handle} AND status = 'earmarked' AND organization_id = {organizationId}
                RETURNING id AS "Value"
                """).ToListAsync(ct);
            var claimId = consumed.Count > 0 ? consumed[0] : Guid.CreateVersion7(now);
            if (consumed.Count == 0)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO gozleme.handle_claims (id, handle, status, organization_id, publisher_id, held_at)
                    VALUES ({claimId}, {handle}, 'held', {organizationId}, {publisher.Id}, {now})
                    """, ct);
            }

            return (WriteResult.Ok, publisher, claimId);
        }
        catch (Exception ex) when (Classify(ex) is { } result)
        {
            await db.Database.RollbackTransactionAsync(ct);
            return (result, null, Guid.Empty);
        }
    }

    /// <summary>One insert; <c>capabilities_reserve_alias</c> reserves <c>handle/name</c> in the same statement.</summary>
    public async Task<(WriteResult Result, Capability? Capability)> CreateCapabilityAsync(
        Publisher publisher, Guid userId, string name, string displayName, CancellationToken ct)
    {
        var now = Now();
        var capability = new Capability
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = publisher.OrganizationId,
            PublisherId = publisher.Id,
            Name = name,
            DisplayName = displayName,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
        };
        try
        {
            db.Capabilities.Add(capability);
            await db.SaveChangesAsync(ct);
            return (WriteResult.Ok, capability);
        }
        catch (Exception ex) when (Classify(ex) is { } result)
        {
            await db.Database.RollbackTransactionAsync(ct);
            return (result, null);
        }
    }

    /// <summary>Changes the display name only; false when it was already that value (no write, no audit).</summary>
    public async Task<bool> RenameAsync(Publisher publisher, string displayName, CancellationToken ct)
    {
        if (string.Equals(publisher.DisplayName, displayName, StringComparison.Ordinal))
        {
            return false;
        }

        publisher.DisplayName = displayName;
        publisher.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RenameAsync(Capability capability, string displayName, CancellationToken ct)
    {
        if (string.Equals(capability.DisplayName, displayName, StringComparison.Ordinal))
        {
            return false;
        }

        capability.DisplayName = displayName;
        capability.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The decided answers (research R8): the live handle index is "unavailable", the name and alias constraints
    /// are "conflict", and any other unique or foreign-key violation is the unknown-ID 404. Row-level refusals
    /// (<c>42501</c>) are not handled here: <c>TenantTransaction</c> answers them with the same 404.
    /// </summary>
    internal static WriteResult? Classify(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) switch
        {
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: HandleUniqueIndex } => WriteResult.HandleUnavailable,
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CapabilityNameIndex or AliasPrimaryKey } => WriteResult.Conflict,
            { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation } => WriteResult.NotFound,
            _ => null,
        };

    /// <summary>
    /// The clock at PostgreSQL's <c>timestamptz</c> precision (microseconds), so a write's response equals every later
    /// read of the stored row; .NET on Linux ticks in 100 ns.
    /// </summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
