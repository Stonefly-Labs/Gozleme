using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Revisions;

/// <summary>A stored revision with its attestation, the attestation's key (public parts only) and that key's retirement.</summary>
internal sealed record StoredRevision(ManifestRevision Revision, RevisionAttestation Attestation, PublisherSigningKey Key, SigningKeyRetirement? Retirement);

/// <summary>
/// Revision and signing-key reads in the request's tenant transaction (GOZ-0007 data-model.md), under forced RLS.
/// Every read query projects the key's public parts only: <c>encrypted_private_key</c> is selected by
/// <see cref="ActiveKeyForSigningAsync"/> alone, which only the publication path calls (plan "Key boundary").
/// A malformed id, an unknown id and an id under another capability or publisher are all null.
/// </summary>
internal sealed class RevisionStore(GozlemeDbContext db)
{
    public const int MaxPageSize = 100;

    public async Task<List<StoredRevision>> ListAsync(Capability capability, int after, int take, CancellationToken ct)
    {
        var revisions = await db.ManifestRevisions.AsNoTracking()
            .Where(r => r.CapabilityId == capability.Id && r.OrganizationId == capability.OrganizationId && r.Sequence > after)
            .OrderBy(r => r.Sequence).Take(take)
            .ToListAsync(ct);
        return await WithAttestationsAsync(revisions, ct);
    }

    public async Task<StoredRevision?> FindAsync(Capability capability, string? revisionId, CancellationToken ct)
    {
        if (!Guid.TryParse(revisionId, out var id))
        {
            return null;
        }

        var revision = await db.ManifestRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.CapabilityId == capability.Id && r.OrganizationId == capability.OrganizationId, ct);
        return revision is null ? null : (await WithAttestationsAsync([revision], ct)).SingleOrDefault();
    }

    /// <summary>The publisher's keys by generation with their retirements; public parts only.</summary>
    public async Task<List<(PublisherSigningKey Key, SigningKeyRetirement? Retirement)>> KeysAsync(Publisher publisher, CancellationToken ct)
    {
        var keys = await PublicKeys().Where(k => k.PublisherId == publisher.Id && k.OrganizationId == publisher.OrganizationId)
            .OrderBy(k => k.Generation).ToListAsync(ct);
        var retirements = await RetirementsAsync(keys.Select(k => k.Id), ct);
        return [.. keys.Select(k => (k, retirements.GetValueOrDefault(k.Id)))];
    }

    public async Task<PublisherSigningKey?> FindKeyAsync(Publisher publisher, string? keyId, CancellationToken ct) =>
        Guid.TryParse(keyId, out var id)
            ? await PublicKeys().FirstOrDefaultAsync(k => k.Id == id && k.PublisherId == publisher.Id && k.OrganizationId == publisher.OrganizationId, ct)
            : null;

    /// <summary>The active key (highest generation) with its encrypted private part: the publication path only.</summary>
    public async Task<PublisherSigningKey?> ActiveKeyForSigningAsync(Publisher publisher, CancellationToken ct) =>
        await db.PublisherSigningKeys.AsNoTracking()
            .Where(k => k.PublisherId == publisher.Id && k.OrganizationId == publisher.OrganizationId)
            .Where(k => !db.SigningKeyRetirements.Any(r => r.KeyId == k.Id)) // a retired key never signs again (fails closed: 503)
            .OrderByDescending(k => k.Generation).FirstOrDefaultAsync(ct);

    private IQueryable<PublisherSigningKey> PublicKeys() =>
        db.PublisherSigningKeys.AsNoTracking().Select(k => new PublisherSigningKey
        {
            Id = k.Id,
            OrganizationId = k.OrganizationId,
            PublisherId = k.PublisherId,
            Generation = k.Generation,
            KeyId = k.KeyId,
            Algorithm = k.Algorithm,
            PublicKey = k.PublicKey,
            CreatedAt = k.CreatedAt,
            CreatedBy = k.CreatedBy,
        });

    private async Task<Dictionary<Guid, SigningKeyRetirement>> RetirementsAsync(IEnumerable<Guid> keyIds, CancellationToken ct)
    {
        var ids = keyIds.Distinct().ToList();
        return await db.SigningKeyRetirements.AsNoTracking().Where(r => ids.Contains(r.KeyId)).ToDictionaryAsync(r => r.KeyId, ct);
    }

    private async Task<List<StoredRevision>> WithAttestationsAsync(List<ManifestRevision> revisions, CancellationToken ct)
    {
        if (revisions.Count == 0)
        {
            return [];
        }

        var ids = revisions.Select(r => r.Id).ToList();
        var attestations = await db.RevisionAttestations.AsNoTracking().Where(a => ids.Contains(a.RevisionId)).ToDictionaryAsync(a => a.RevisionId, ct);
        var keyIds = attestations.Values.Select(a => a.SigningKeyId).Distinct().ToList();
        var keys = await PublicKeys().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        var retirements = await RetirementsAsync(keyIds, ct);

        // The deferred trigger makes an unattested revision impossible to commit; a row without one is skipped, never shown verified.
        return [.. revisions
            .Where(r => attestations.TryGetValue(r.Id, out var a) && keys.ContainsKey(a.SigningKeyId))
            .Select(r =>
            {
                var attestation = attestations[r.Id];
                return new StoredRevision(r, attestation, keys[attestation.SigningKeyId], retirements.GetValueOrDefault(attestation.SigningKeyId));
            })];
    }
}
