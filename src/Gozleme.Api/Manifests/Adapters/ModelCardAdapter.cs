using System.Globalization;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>model/1</c>: the Gözleme model card v1 (contracts/model-card-v1.schema.json), closed, with <c>^x-</c> members at
/// the root and in <c>modalities</c> and <c>limits</c>. Every name at the root and in those closed objects is a
/// reserved-vocabulary position, so a reserved name there is <c>reserved_claim</c> beside <c>unknown_field</c>
/// (maintainer decision 2026-11-17, "extend to nested", T095).
/// </summary>
internal sealed class ModelCardAdapter : IProtocolAdapter
{
    public string Id => "model/1";

    public string Family => "model";

    public IReadOnlyList<NativeFormat> Formats { get; } = [new("gozleme-model-card", "1", [NativeEncoding.Json])];

    public int CountEntries(ManifestNode tree) => 0;

    public void Validate(ManifestNode tree, NativeContext context) => context.Schema(SchemaIds.ModelCard, tree);

    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) =>
        Members(tree).Select(m => new NamePosition(m.Key, m.Value.Pointer))
            .Concat(Members(tree).Where(IsExtension).SelectMany(m => AllNames(m.Value)))
            .Concat(new[] { tree["modalities"], tree["limits"] }.SelectMany(n => Members(n))
                .SelectMany(m => (IsExtension(m) ? AllNames(m.Value) : []).Prepend(new NamePosition(m.Key, m.Value.Pointer))));

    private static bool IsExtension(KeyValuePair<string, ManifestNode> member) => member.Key.StartsWith("x-", StringComparison.Ordinal);

    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree) => [];

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) => [];

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
    {
        var modalities = tree["modalities"]!;
        var limits = tree["limits"];
        var builder = new ProjectionBuilder(Id, Family, "model", envelope)
        {
            Attributes = new ProjectionAttributes
            {
                InputModalities = [.. Items(modalities["input"]).Select(i => i.Text!)],
                OutputModalities = [.. Items(modalities["output"]).Select(i => i.Text!)],
                ContextWindowTokens = Integer(limits?["contextWindowTokens"]),
                MaxOutputTokens = Integer(limits?["maxOutputTokens"]),
            },
        };
        return builder.Build(tree.StringOf("name")!, tree.StringOf("title"), tree.StringOf("description"));
    }

    /// <summary>A schema-valid integer (1–1,000,000,000; JSON may write it as <c>1e3</c> or <c>1000.0</c>).</summary>
    private static long? Integer(ManifestNode? node) =>
        node is { Kind: NodeKind.Number, Text: { } text } ? (long)decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : null;
}
