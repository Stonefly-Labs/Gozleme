namespace Gozleme.Api.Children;

/// <summary>
/// A child identity (GOZ-0008 data-model.md, contracts/child-identity.md): one declared entry of a capability,
/// matched across revisions by exact (<see cref="Kind"/>, <see cref="NativeName"/>). Only <see cref="Id"/> is stored
/// in the child ID. Write-once for every login (migration ChildIdentities); the match digest column is derived by the
/// insert guard and not mapped.
/// </summary>
internal sealed class CapabilityChild
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid CapabilityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    /// <summary>The match key with <see cref="Kind"/>; never in an ID, URL, span or log (FR-019).</summary>
    public string NativeName { get; set; } = string.Empty;

    /// <summary>An install-step duplicate (FR-016a): never matched, so absent from every later revision.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>Gap-free per capability; stable, name-free order and paging only (A6).</summary>
    public int Ordinal { get; set; }

    public Guid FirstRevisionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A child revision record: the entry at <see cref="EntryIndex"/> of a revision's stored projection is <see cref="ChildId"/>.</summary>
internal sealed class RevisionChild
{
    public Guid RevisionId { get; set; }

    public int EntryIndex { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ChildId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string NativeName { get; set; } = string.Empty;

    /// <summary>The generation 1 entry title (A8); null when the entry has none.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// A projection generation (contracts/projection-generations.md). Generation 1 holds no bytes: it is the revision's
/// stored, attested projection (FR-009). Later generations hold their own bytes and are not attested.
/// </summary>
internal sealed class ProjectionGeneration
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RevisionId { get; set; }

    public int Generation { get; set; }

    public string AdapterId { get; set; } = string.Empty;

    public string AdapterVersion { get; set; } = string.Empty;

    public string ProjectionVersion { get; set; } = string.Empty;

    public byte[]? Projection { get; set; }

    public string? ProjectionDigest { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
