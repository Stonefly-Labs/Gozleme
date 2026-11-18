using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>skill/1</c>: Agent Skills <c>SKILL.md</c>, snapshot 2026-11-17. The tree is the front matter; the body is scanned
/// for secrets only. The upstream rule that <c>name</c> equals the parent directory is not enforced: a single document
/// has no directory. <c>metadata</c> is the extension mechanism.
/// </summary>
internal sealed class AgentSkillsAdapter : IProtocolAdapter
{
    public string Id => "skill/1";

    public string Family => "skill";

    public IReadOnlyList<NativeFormat> Formats { get; } = [new("agent-skills", "2026-11-17", [NativeEncoding.Markdown])];

    public int CountEntries(ManifestNode tree) => 0;

    public void Validate(ManifestNode tree, NativeContext context) => context.Schema(SchemaIds.AgentSkills, tree);

    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) =>
        Members(tree).Select(m => new NamePosition(m.Key, m.Value.Pointer)).Concat(MembersAndBeneath(tree["metadata"]));

    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree) => [];

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) => [];

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope) =>
        new ProjectionBuilder(Id, Family, "agent-skill", envelope).Build(tree.StringOf("name")!, null, tree.StringOf("description"));
}
