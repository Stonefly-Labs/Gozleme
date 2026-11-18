using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>Native encodings an envelope may declare (clarify Q2).</summary>
internal static class NativeEncoding
{
    public const string Json = "json";
    public const string Yaml = "yaml";
    public const string Markdown = "markdown";
}

/// <summary>One row of the family → format matrix (contracts/native-formats.md).</summary>
internal sealed record NativeFormat(string Format, string FormatVersion, IReadOnlyList<string> Encodings);

/// <summary>A name in a position the reserved vocabulary applies to, located at the member's own pointer.</summary>
internal sealed record NamePosition(string Name, JsonPointer Pointer);

/// <summary>What an adapter reads besides the tree: the validated envelope, the schema registry and the problem sink.</summary>
internal sealed class NativeContext(ManifestEnvelope envelope, SchemaRegistry schemas, List<ManifestProblem> problems)
{
    public ManifestEnvelope Envelope { get; } = envelope;

    public SchemaRegistry Schemas { get; } = schemas;

    public List<ManifestProblem> Problems { get; } = problems;

    public void Add(ManifestRule rule, JsonPointer pointer) => Problems.Add(new ManifestProblem(rule, ProblemLocation.At(ManifestPart.Native, pointer)));

    /// <summary>Validates the whole tree against a bundled schema and collects its problems.</summary>
    public void Schema(string uri, ManifestNode tree) => Problems.AddRange(Schemas.Validate(uri, tree.ToJson(), ManifestPart.Native));
}

/// <summary>
/// One protocol adapter (data-model.md ProtocolAdapter): a stateless singleton with no I/O that validates one
/// family's native tree and projects it. A new native version is a new <see cref="Formats"/> row or a new
/// adapter <see cref="Id"/>, never a change to an existing one (FR-005).
/// </summary>
internal interface IProtocolAdapter
{
    /// <summary><c>mcp/1</c>, <c>a2a/1</c>, <c>api/1</c>, <c>event/1</c>, <c>skill/1</c> or <c>model/1</c>.</summary>
    string Id { get; }

    string Family { get; }

    IReadOnlyList<NativeFormat> Formats { get; }

    /// <summary>The entries the projection would list (the 1,000-entry bound).</summary>
    int CountEntries(ManifestNode tree);

    /// <summary>Version consistency, schema and adapter rules; problems are collected, never thrown.</summary>
    void Validate(ManifestNode tree, NativeContext context);

    /// <summary>Names inside the format's extension mechanism and the adapter-listed fields (research R6).</summary>
    IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree);

    /// <summary>Secret-bearing fields: any non-empty value is <c>secret.field</c> unless it is a reference (research R7).</summary>
    IEnumerable<ManifestNode> SecretFields(ManifestNode tree);

    /// <summary>Subtrees that are JSON Schemas or documents whose <c>$ref</c> values are checked (research R10).</summary>
    IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree);

    /// <summary>The projection v1 of an accepted tree, from typed values only.</summary>
    ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope);
}
