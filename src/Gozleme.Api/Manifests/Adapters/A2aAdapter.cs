using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>a2a/1</c>: the A2A Agent Card 1.0 (v1.0.1), validated against the generated per-message schemas through the
/// Gözleme required-field overlay. <c>/signatures</c> is reserved (GOZ-0007 signs); <c>securitySchemes</c> and
/// <c>securityRequirements</c> are declarations and accepted. <c>provider</c> is descriptive, never projected or trusted as
/// owner (maintainer decision 2026-11-17). Proto JSON accepts snake_case field names too.
/// </summary>
internal sealed class A2aAdapter : IProtocolAdapter
{
    public string Id => "a2a/1";

    public string Family => "a2a";

    public IReadOnlyList<NativeFormat> Formats { get; } = [new("a2a-agent-card", "1.0", [NativeEncoding.Json])];

    public int CountEntries(ManifestNode tree) => Count(tree["skills"]);

    public void Validate(ManifestNode tree, NativeContext context)
    {
        context.Schema(SchemaIds.A2aStrict, tree);
        if (tree["signatures"] is { } signatures)
        {
            context.Add(ManifestRules.ReservedA2aSignatures, signatures.Pointer);
        }
    }

    /// <summary>Member names inside <c>capabilities.extensions[].params</c>, at any depth.</summary>
    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) =>
        Items(tree["capabilities"]?["extensions"]).SelectMany(e => AllNames(e["params"]));

    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree) => [];

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) => [];

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
    {
        var interfaces = Items(tree["supportedInterfaces"] ?? tree["supported_interfaces"]);
        var builder = new ProjectionBuilder(Id, Family, "a2a-agent", envelope)
        {
            Attributes = new ProjectionAttributes
            {
                ProtocolBindings = Distinct(interfaces.Select(i => i.StringOf("protocolBinding") ?? i.StringOf("protocol_binding"))),
            },
        };
        foreach (var skill in Items(tree["skills"]))
        {
            builder.Entry("skill", skill.StringOf("id")!, skill.StringOf("name"));
        }

        return builder.Build(tree.StringOf("name")!, null, tree.StringOf("description"));
    }
}
