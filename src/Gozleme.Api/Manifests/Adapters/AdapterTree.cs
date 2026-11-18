using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>Small tree helpers shared by the adapters; all pure.</summary>
internal static class AdapterTree
{
    /// <summary>The header names whose values are credentials (contracts/native-formats.md, research R7).</summary>
    public static readonly string[] SecretHeaderNames = ["Authorization", "Proxy-Authorization", "Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token"];

    /// <summary>Keys whose values are data, not structure: extension names are not looked for beneath them.</summary>
    private static readonly string[] DataKeys = ["example", "examples", "default", "enum", "const"];

    /// <summary>Keys whose member names are user data, though their values are structure (schema property maps).</summary>
    private static readonly string[] DataNameKeys = ["properties", "patternProperties"];

    public static bool IsSecretHeader(string? name) => name is not null && SecretHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<ManifestNode> Items(ManifestNode? node) => node is { IsArray: true } ? node.Items : [];

    public static IEnumerable<KeyValuePair<string, ManifestNode>> Members(ManifestNode? node) => node is { IsObject: true } ? node.Members : [];

    public static int Count(ManifestNode? node) => node is { IsArray: true } ? node.Items.Count : node is { IsObject: true } ? node.Members.Count : 0;

    /// <summary>Distinct string values, in first-seen order.</summary>
    public static List<string> Distinct(IEnumerable<string?> values) => [.. values.OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Member names that start with <c>x-</c> in structural positions (OpenAPI, AsyncAPI, model card): not below an
    /// example, default, enum or const value, and not the property names of a schema, which are user data (research R6).
    /// Every member name inside an extension's value is included too (T075, R6 rule 1).
    /// </summary>
    public static IEnumerable<NamePosition> ExtensionNames(ManifestNode node)
    {
        foreach (var (name, value) in Members(node))
        {
            if (name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
            {
                yield return new NamePosition(name, value.Pointer);
                foreach (var inner in AllNames(value))
                {
                    yield return inner;
                }

                continue;
            }

            if (DataKeys.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            if (DataNameKeys.Contains(name, StringComparer.Ordinal))
            {
                foreach (var inner in Members(value).SelectMany(m => Walk(m.Value)))
                {
                    yield return inner;
                }

                continue;
            }

            foreach (var inner in Walk(value))
            {
                yield return inner;
            }
        }

        foreach (var item in Items(node))
        {
            foreach (var inner in ExtensionNames(item))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<NamePosition> Walk(ManifestNode value) => value.IsObject || value.IsArray ? ExtensionNames(value) : [];

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
