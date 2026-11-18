using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Rules;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>Small tree helpers shared by the adapters; all pure.</summary>
internal static class AdapterTree
{
    /// <summary>The header names whose values are credentials (contracts/native-formats.md, research R7).</summary>
    public static readonly string[] SecretHeaderNames = ["Authorization", "Proxy-Authorization", "Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token"];

    public static bool IsSecretHeader(string? name) => name is not null && SecretHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<ManifestNode> Items(ManifestNode? node) => node is { IsArray: true } ? node.Items : [];

    public static IEnumerable<KeyValuePair<string, ManifestNode>> Members(ManifestNode? node) => node is { IsObject: true } ? node.Members : [];

    public static int Count(ManifestNode? node) => node is { IsArray: true } ? node.Items.Count : node is { IsObject: true } ? node.Members.Count : 0;

    /// <summary>Distinct string values, in first-seen order.</summary>
    public static List<string> Distinct(IEnumerable<string?> values) => [.. values.OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Member names that start with <c>x-</c> in structural positions (OpenAPI, AsyncAPI): sample data is skipped by
    /// position (<see cref="DataPositions"/>, T083), and the property names of a schema are user data (research R6).
    /// Every member name inside an extension's value is included too (T075, R6 rule 1).
    /// </summary>
    public static IEnumerable<NamePosition> ExtensionNames(ManifestNode node, DataPositions positions, PositionKind kind = PositionKind.Object)
    {
        foreach (var (name, value, childKind) in positions.Members(node, kind))
        {
            if (kind != PositionKind.DataNameMap && name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            {
                yield return new NamePosition(name, value.Pointer);
                foreach (var inner in AllNames(value))
                {
                    yield return inner;
                }

                continue;
            }

            if (value.IsObject || value.IsArray)
            {
                foreach (var inner in ExtensionNames(value, positions, childKind))
                {
                    yield return inner;
                }
            }
        }

        foreach (var item in Items(node))
        {
            foreach (var inner in ExtensionNames(item, positions))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Each member name of <paramref name="node"/> and every member name beneath it (an extension map and its values).</summary>
    public static IEnumerable<NamePosition> MembersAndBeneath(ManifestNode? node) =>
        Members(node).SelectMany(m => AllNames(m.Value).Prepend(new NamePosition(m.Key, m.Value.Pointer)));

    /// <summary>Every member name in a subtree (free-form maps such as A2A extension <c>params</c>).</summary>
    public static IEnumerable<NamePosition> AllNames(ManifestNode? node) =>
        node is null ? [] : node.Descendants().SelectMany(n => n.Members).Select(m => new NamePosition(m.Key, m.Value.Pointer));

    /// <summary>The <c>value</c> and <c>default</c> of a declared input, the fields a credential would be put in.</summary>
    public static IEnumerable<ManifestNode> ValueAndDefault(ManifestNode input) => new[] { input["value"], input["default"] }.OfType<ManifestNode>();

    /// <summary>An example-bearing object's <c>example</c>, <c>examples.*.value</c> (or array items) and <c>default</c>, also inside its <c>schema</c>.</summary>
    public static IEnumerable<ManifestNode> ExampleValues(ManifestNode node)
    {
        foreach (var holder in new[] { node, node["schema"] }.OfType<ManifestNode>())
        {
            foreach (var field in new[] { holder["example"], holder["default"] }.OfType<ManifestNode>())
            {
                yield return field;
            }

            if (holder["examples"] is { } examples)
            {
                foreach (var item in examples.Items)
                {
                    yield return item;
                }

                foreach (var (_, example) in Members(examples))
                {
                    if (example["value"] is { } value)
                    {
                        yield return value;
                    }
                }
            }
        }
    }
}
