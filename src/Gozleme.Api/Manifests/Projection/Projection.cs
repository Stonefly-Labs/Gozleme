using System.Text.Json.Serialization;

namespace Gozleme.Api.Manifests.Projection;

/// <summary>
/// Normalized projection v1 (contracts/projection-v1.md, research R12): a Gözleme summary derived from the native
/// tree, versioned on its own, closed, with no child identity (GOZ-0008). Nothing in it flows back into the native
/// document. <c>title</c> and <c>description</c> are written as <c>null</c> when absent.
/// </summary>
internal sealed record ManifestProjection(
    string ProjectionVersion,
    string Adapter,
    string Family,
    string Kind,
    string NativeName,
    string? Title,
    string? Description,
    string DeclaredVersion,
    ProjectionAttributes Attributes,
    IReadOnlyList<ProjectionEntry> Entries,
    IReadOnlyList<string> Derived)
{
    public const string Version = "1";
}

/// <summary>The closed per-family attribute set; members a family does not use are omitted.</summary>
internal sealed record ProjectionAttributes
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolRevision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Transports { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ProtocolBindings { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenapiVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AsyncapiVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InputModalities { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OutputModalities { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextWindowTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxOutputTokens { get; init; }
}

/// <summary>One declared entry, in native document order. <c>title</c> is omitted when the native entry has none.</summary>
internal sealed record ProjectionEntry(
    string Kind,
    string NativeName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title);
