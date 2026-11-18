using Gozleme.Api.Manifests;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions.Signing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Revisions;

internal enum PublishResult
{
    Published,
    VersionExists,
    Suspended,
    Unavailable,
}

/// <summary>A committed-on-success publication: the stored revision, its attestation and key, and whether the key is new.</summary>
internal sealed record Publication(PublishResult Result, StoredRevision? Revision = null, bool KeyCreated = false);

/// <summary>
/// Publishes an accepted manifest in the request's tenant transaction (GOZ-0007 research R7, R8, R12 steps 10–12):
/// the shared per-publisher signing lock (a succession holds it exclusively), then the per-capability revision lock;
/// version uniqueness by SemVer precedence (contents never compared, identical replays included); the gap-free next
/// sequence; the publisher's active key, created lazily as generation 1 inside this transaction; the canonical
/// projection and its digest; statement v1, signed; then the revision and its attestation. Everything commits together
/// or not at all, and a refusal rolls the transaction back so <c>TenantTransaction</c> commits nothing.
/// </summary>
internal sealed class RevisionPublisher(GozlemeDbContext db, RevisionStore store, SigningKeyVault vault)
{
    public const string VersionKeyIndex = "ix_manifest_revisions_capability_id_version_key";

    public async Task<Publication> PublishAsync(
        Publisher publisher, Capability capability, AcceptedResult accepted, string origin, Guid accountId, CancellationToken ct)
    {
        try
        {
            // now() is the transaction's start time: the trigger writes the same value as published_at (R4).
            var publishedAt = await Scalar<DateTimeOffset>($"SELECT now() AS \"Value\"", ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended('gozleme.signing:' || {publisher.Id.ToString()}, 0))", ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('gozleme.revisions:' || {capability.Id.ToString()}, 0))", ct);

            var versionKey = VersionKey.From(accepted.Envelope.Version);
            if (await db.ManifestRevisions.AnyAsync(r => r.CapabilityId == capability.Id && r.VersionKey == versionKey, ct))
            {
                return await RefuseAsync(PublishResult.VersionExists, ct);
            }

            var sequence = 1 + (await db.ManifestRevisions.Where(r => r.CapabilityId == capability.Id).MaxAsync(r => (int?)r.Sequence, ct) ?? 0);
            var (key, created) = await ActiveKeyAsync(publisher, accountId, ct);
            if (key is null)
            {
                return await RefuseAsync(PublishResult.Unavailable, ct);
            }

            var nativeBytes = EnvelopeReader.NativeBytes(accepted.Native.Document);
            var projection = CanonicalJson.Canonicalize(ManifestJson.ProjectionNode(accepted.Projection));
            var (adapterId, adapterVersion) = AttestationStatement.SplitAdapter(accepted.Projection.Adapter);
            var revision = new ManifestRevision
            {
                Id = Guid.CreateVersion7(publishedAt),
                OrganizationId = capability.OrganizationId,
                PublisherId = publisher.Id,
                CapabilityId = capability.Id,
                Sequence = sequence,
                Version = accepted.Envelope.Version,
                VersionKey = versionKey,
                Family = accepted.Envelope.Family,
                EnvelopeSummary = ManifestJson.SummaryJson(accepted.Envelope),
                NativeFormat = accepted.Native.Format,
                NativeFormatVersion = accepted.Native.FormatVersion,
                NativeEncoding = accepted.Native.Encoding,
                NativeDocument = nativeBytes,
                NativeDigest = RevisionVerifier.Digest(nativeBytes),
                NativeByteLength = nativeBytes.Length,
                Projection = projection,
                ProjectionVersion = accepted.Projection.ProjectionVersion,
                ProjectionDigest = RevisionVerifier.Digest(projection),
                AdapterId = adapterId,
                AdapterVersion = adapterVersion,
                RuleCatalogueVersion = ManifestRules.CatalogueVersion,
                SchemaBundleVersion = SchemaRegistry.BundleVersion,
                PublishedBy = accountId,
                PublishedAt = publishedAt,
            };

            var statement = AttestationStatement.For(revision, origin, key.KeyId).ToCanonicalBytes();
            if (!vault.TrySign(key, statement, out var signature))
            {
                return await RefuseAsync(PublishResult.Unavailable, ct);
            }

            var attestation = new RevisionAttestation
            {
                RevisionId = revision.Id,
                OrganizationId = revision.OrganizationId,
                SigningKeyId = key.Id,
                StatementVersion = AttestationStatement.CurrentVersion,
                Statement = statement,
                Signature = signature!,
                CreatedAt = publishedAt,
            };
            db.ManifestRevisions.Add(revision);
            db.RevisionAttestations.Add(attestation);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return new Publication(PublishResult.Published, new StoredRevision(revision, attestation, PublicPart(key), null), created);
        }
        catch (Exception ex) when (Classify(ex) is { } result)
        {
            db.ChangeTracker.Clear();
            return await RefuseAsync(result, ct);
        }
    }

    /// <summary>
    /// The active key, or generation 1 created now (R7): <c>ON CONFLICT DO NOTHING</c> makes a concurrent first
    /// publication wait on the unique entry and then read the committed key, so exactly one key exists. Null when no
    /// usable KEK is configured.
    /// </summary>
    private async Task<(PublisherSigningKey? Key, bool Created)> ActiveKeyAsync(Publisher publisher, Guid accountId, CancellationToken ct)
    {
        if (await store.ActiveKeyForSigningAsync(publisher, ct) is { } existing)
        {
            return (existing, false);
        }

        if (!vault.TryCreateKey(publisher.Id, out var fresh))
        {
            return (null, false);
        }

        var id = Guid.CreateVersion7();
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO gozleme.publisher_signing_keys (id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key, created_at, created_by)
            VALUES ({id}, {publisher.OrganizationId}, {publisher.Id}, 1, {fresh!.KeyId}, {Ed25519Signer.Algorithm}, {fresh.PublicKey}, {fresh.EncryptedPrivateKey}, now(), {accountId})
            ON CONFLICT (publisher_id, generation) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        return (await store.ActiveKeyForSigningAsync(publisher, ct), inserted.Count == 1);
    }

    private async Task<Publication> RefuseAsync(PublishResult result, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await db.Database.RollbackTransactionAsync(ct);
        }

        return new Publication(result);
    }

    private async Task<T> Scalar<T>(FormattableString sql, CancellationToken ct) => (await db.Database.SqlQuery<T>(sql).ToListAsync(ct)).Single();

    private static PublisherSigningKey PublicPart(PublisherSigningKey key) => new()
    {
        Id = key.Id,
        OrganizationId = key.OrganizationId,
        PublisherId = key.PublisherId,
        Generation = key.Generation,
        KeyId = key.KeyId,
        Algorithm = key.Algorithm,
        PublicKey = key.PublicKey,
        CreatedAt = key.CreatedAt,
        CreatedBy = key.CreatedBy,
    };

    /// <summary>
    /// The defence-in-depth answers (R8, R12): a unique violation on the revision keys is <c>version_exists</c>, and a
    /// row-level refusal (<c>42501</c>, for example a reclaim that raced the suspension check) is <c>suspended</c>.
    /// </summary>
    internal static PublishResult? Classify(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) switch
        {
            { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "manifest_revisions" } => PublishResult.VersionExists,
            { SqlState: PostgresErrorCodes.InsufficientPrivilege } => PublishResult.Suspended,
            _ => null,
        };
}
