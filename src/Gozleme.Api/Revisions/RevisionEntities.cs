namespace Gozleme.Api.Revisions;

/// <summary>
/// A manifest revision (GOZ-0007 data-model.md, FR-002): the native document exactly as published, its canonical
/// projection and the versions that produced them. Write-once for every login (migration ManifestRevisions); the
/// verification result is never stored (FR-010).
/// </summary>
internal sealed class ManifestRevision
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid PublisherId { get; set; }

    public Guid CapabilityId { get; set; }

    public int Sequence { get; set; }

    public string Version { get; set; } = string.Empty;

    public string VersionKey { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    /// <summary>The GOZ-0006 envelope summary as JSON text (jsonb); not signed.</summary>
    public string EnvelopeSummary { get; set; } = string.Empty;

    public string NativeFormat { get; set; } = string.Empty;

    public string NativeFormatVersion { get; set; } = string.Empty;

    public string NativeEncoding { get; set; } = string.Empty;

    public byte[] NativeDocument { get; set; } = [];

    public string NativeDigest { get; set; } = string.Empty;

    public int NativeByteLength { get; set; }

    /// <summary>Canonical (RFC 8785) projection v1 bytes: the stored bytes, the download and the digest input (R3).</summary>
    public byte[] Projection { get; set; } = [];

    public string ProjectionVersion { get; set; } = string.Empty;

    public string ProjectionDigest { get; set; } = string.Empty;

    public string AdapterId { get; set; } = string.Empty;

    public string AdapterVersion { get; set; } = string.Empty;

    public string RuleCatalogueVersion { get; set; } = string.Empty;

    public string SchemaBundleVersion { get; set; } = string.Empty;

    /// <summary>Opaque account id, pinned to the session account by policy; no foreign key (R11).</summary>
    public Guid PublishedBy { get; set; }

    /// <summary>Set to the transaction's <c>now()</c> by trigger; equal to the statement's <c>publishedAt</c>.</summary>
    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>The signed statement v1 bytes and their Ed25519 signature for one revision (FR-008).</summary>
internal sealed class RevisionAttestation
{
    public Guid RevisionId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid SigningKeyId { get; set; }

    public short StatementVersion { get; set; }

    public byte[] Statement { get; set; } = [];

    public byte[] Signature { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A registry-held publisher signing key (contracts/signing-keys.md). The active key is the highest generation. The
/// private seed exists only as AES-256-GCM ciphertext under the KEK, read by <c>SigningKeyVault</c>
/// alone; no read query projects it.
/// </summary>
internal sealed class PublisherSigningKey
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid PublisherId { get; set; }

    public int Generation { get; set; }

    /// <summary><c>sha256:</c> + lowercase hex of the 32-byte raw public key (R5).</summary>
    public string KeyId { get; set; } = string.Empty;

    public string Algorithm { get; set; } = string.Empty;

    public byte[] PublicKey { get; set; } = [];

    public byte[] EncryptedPrivateKey { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The account whose first publication created generation 1; null for operator successors (R11).</summary>
    public Guid? CreatedBy { get; set; }
}

/// <summary>The append-only retirement of a key by operator succession (Q5, R9).</summary>
internal sealed class SigningKeyRetirement
{
    public Guid KeyId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid SuccessorKeyId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset RetiredAt { get; set; }
}
