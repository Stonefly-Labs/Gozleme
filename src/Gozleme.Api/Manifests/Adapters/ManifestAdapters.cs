namespace Gozleme.Api.Manifests.Adapters;

/// <summary>The six adapters, one per family (contracts/native-formats.md).</summary>
internal static class ManifestAdapters
{
    public static IReadOnlyList<IProtocolAdapter> All { get; } =
    [
        new McpAdapter(), new A2aAdapter(), new OpenApiAdapter(), new AsyncApiAdapter(), new AgentSkillsAdapter(), new ModelCardAdapter(),
    ];
}
