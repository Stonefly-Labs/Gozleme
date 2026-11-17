using System.Text.RegularExpressions;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>mcp/1</c>: the Gözleme MCP server manifest v1 (maintainer P4, contracts/native-formats.md). Primitives are typed by
/// the core schema of the declared <c>protocolRevision</c>; the mode is derived from it, never declared. Primitive
/// lists are publisher-declared and advisory: nothing is contacted.
/// </summary>
internal sealed partial class McpAdapter : IProtocolAdapter
{
    public const string Stateless = "2026-07-28";
    public const string Stateful = "2025-11-25";

    private const string McpNamespace = "io.modelcontextprotocol/";
    private const string RegistryNamespace = "io.modelcontextprotocol.registry/";
    private const string PublisherProvided = RegistryNamespace + "publisher-provided";

    private static readonly (string List, string Kind, string Capability)[] Primitives =
    [
        ("tools", "tool", "tools"), ("resources", "resource", "resources"), ("resourceTemplates", "resource-template", "resources"), ("prompts", "prompt", "prompts"),
    ];

    public string Id => "mcp/1";

    public string Family => "mcp";

    public IReadOnlyList<NativeFormat> Formats { get; } = [new("mcp-server-manifest", "1", [NativeEncoding.Json])];

    public int CountEntries(ManifestNode tree) => Primitives.Sum(p => Count(tree[p.List]));

    public void Validate(ManifestNode tree, NativeContext context)
    {
        if (tree["protocolRevision"] is { IsString: true, Text: not (Stateless or Stateful) } revision)
        {
            context.Add(ManifestRules.NativeVersionMismatch, revision.Pointer);
        }

        context.Schema(SchemaIds.McpServerManifest, tree);
        foreach (var (list, _, capability) in Primitives)
        {
            if (Count(tree[list]) > 0 && tree["capabilities"]?[capability] is not { IsObject: true })
            {
                context.Add(ManifestRules.NativeConsistency, tree[list]!.Pointer);
            }

            Unique(tree[list], "name", context);
        }

        Unique(tree["resources"], "uri", context);
        foreach (var meta in MetaObjects(tree))
        {
            foreach (var (key, value) in meta.Members)
            {
                if (key.StartsWith(McpNamespace, StringComparison.Ordinal))
                {
                    context.Add(ManifestRules.ReservedMcpNamespace, value.Pointer);
                }
                else if (key.StartsWith(RegistryNamespace, StringComparison.Ordinal) && key != PublisherProvided)
                {
                    context.Add(ManifestRules.ReservedMcpRegistryMeta, value.Pointer);
                }
                else if (!MetaKey().IsMatch(key))
                {
                    context.Add(ManifestRules.NativeMetaKey, value.Pointer);
                }
            }
        }
    }

    /// <summary>The vocabulary applies to <c>_meta</c> keys at every level.</summary>
    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) =>
        MetaObjects(tree).SelectMany(m => m.Members).Select(m => new NamePosition(m.Key, m.Value.Pointer));

    /// <summary>
    /// <c>value</c>/<c>default</c> of <c>remotes[].headers[]</c>, <c>remotes[].variables{}</c> and
    /// <c>packages[].environmentVariables[]</c> entries declared <c>isSecret</c>, or headers with a credential name.
    /// </summary>
    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree)
    {
        foreach (var remote in Items(tree["remotes"]))
        {
            foreach (var header in Items(remote["headers"]).Where(h => IsSecret(h) || IsSecretHeader(h.StringOf("name"))))
            {
                foreach (var field in ValueAndDefault(header))
                {
                    yield return field;
                }
            }

            foreach (var (_, variable) in Members(remote["variables"]).Where(v => IsSecret(v.Value)))
            {
                foreach (var field in ValueAndDefault(variable))
                {
                    yield return field;
                }
            }
        }

        foreach (var variable in Items(tree["packages"]).SelectMany(p => Items(p["environmentVariables"])).Where(IsSecret))
        {
            foreach (var field in ValueAndDefault(variable))
            {
                yield return field;
            }
        }
    }

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) =>
        Items(tree["tools"]).SelectMany(t => new[] { t["inputSchema"], t["outputSchema"] }).OfType<ManifestNode>();

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
    {
        var revision = tree.StringOf("protocolRevision")!;
        var info = tree["serverInfo"]!;
        var builder = new ProjectionBuilder(Id, Family, "mcp-server", envelope)
        {
            Attributes = new ProjectionAttributes
            {
                ProtocolRevision = revision,
                Mode = revision == Stateless ? "stateless" : "stateful",
                Transports = Distinct(Items(tree["remotes"]).Select(r => r.StringOf("type"))
                    .Concat(Items(tree["packages"]).Select(p => p["transport"]?.StringOf("type")))),
            },
        };
        builder.DeriveAttribute("mode");
        foreach (var (list, kind, _) in Primitives)
        {
            foreach (var item in Items(tree[list]))
            {
                builder.Entry(kind, item.StringOf("name")!, item.StringOf("title"));
            }
        }

        return builder.Build(info.StringOf("name")!, info.StringOf("title"), info.StringOf("description"));
    }

    private static bool IsSecret(ManifestNode input) => input["isSecret"] is { Kind: NodeKind.Boolean, Boolean: true };

    /// <summary><c>_meta</c> at the root, on <c>serverInfo</c> and on every primitive.</summary>
    private static IEnumerable<ManifestNode> MetaObjects(ManifestNode tree) =>
        new[] { tree, tree["serverInfo"] }.Concat(Primitives.SelectMany(p => Items(tree[p.List])))
            .Select(n => n?["_meta"]).OfType<ManifestNode>().Where(m => m.IsObject);

    private static void Unique(ManifestNode? list, string member, NativeContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items(list))
        {
            if (item[member] is { IsString: true, Text: { } value } node && !seen.Add(value))
            {
                context.Add(ManifestRules.NativeConsistency, node.Pointer);
            }
        }
    }

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+/.+$", RegexOptions.CultureInvariant)]
    private static partial Regex MetaKey();
}
