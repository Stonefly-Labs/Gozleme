using System.Text.Json.Serialization;

namespace Gozleme.Api.Children;

/// <summary>Bounded generation comparison values (contracts/projection-generations.md).</summary>
internal static class GenerationComparison
{
    public const string Identical = "identical";
    public const string Different = "different";
    public const string NotComparable = "not_comparable";
}

/// <summary>The latest revision that declares the child, with its generation 1 native name and title (A8).</summary>
internal sealed record ChildLatest(Guid RevisionId, int Sequence, string Version, string NativeName, string? Title);

/// <summary>One child (contracts/children-api.openapi.yaml <c>ChildSummary</c>); <c>id</c> is the route ID, <c>canonicalId</c> the gz:// form.</summary>
internal sealed record ChildSummary(
    Guid Id, string CanonicalId, string Kind, int Ordinal, bool Duplicate, Guid FirstRevisionId, ChildLatest Latest, int PresentIn, DateTimeOffset CreatedAt);

/// <summary>Every child view carries <c>declaredNotVerified: true</c>: children are declared by the publisher, not verified behaviour (FR-014).</summary>
internal sealed record ChildList(IReadOnlyList<ChildSummary> Items, int? Next, bool DeclaredNotVerified = true);

internal sealed record ChildCapability(Guid Id, string CanonicalId, string DisplayName);

/// <summary>One revision of the capability: the child is present (with its native name and title there) or absent.</summary>
internal sealed record ChildHistoryEntry(Guid RevisionId, int Sequence, string Version, bool Present, string? NativeName, string? Title);

internal sealed record ChildDetail(ChildSummary Child, ChildCapability Capability, IReadOnlyList<ChildHistoryEntry> History, int? Next, bool DeclaredNotVerified = true);

internal sealed record RevisionChildView(int EntryIndex, Guid ChildId, string CanonicalId, string Kind, string NativeName, string? Title, bool Duplicate);

internal sealed record RevisionChildList(IReadOnlyList<RevisionChildView> Items, int? Next, bool DeclaredNotVerified = true);

internal sealed record GenerationAdapter(string Id, string Version);

/// <summary>
/// One projection generation. <c>attested</c> is derived (generation 1 only); <c>comparedToPrevious</c> is absent for
/// generation 1 and compares digests only within one adapter ID, adapter version and projection version.
/// </summary>
internal sealed record ProjectionGenerationView(
    Guid Id,
    int Generation,
    GenerationAdapter Adapter,
    string ProjectionVersion,
    string Digest,
    int ByteLength,
    bool Attested,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparedToPrevious);

internal sealed record GenerationList(IReadOnlyList<ProjectionGenerationView> Items);
