using System.Text.RegularExpressions;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>api/1</c>: OpenAPI 3.1 (schema 2026-11-05) and 3.0 (schema 2024-10-18). The document's <c>openapi</c> must be in
/// the declared line. <c>^x-</c> members are the extension mechanism; native <c>deprecated</c> flags are element
/// metadata, not reserved (agent choice A1).
/// </summary>
internal sealed partial class OpenApiAdapter : IProtocolAdapter
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public string Id => "api/1";

    public string Family => "api";

    public IReadOnlyList<NativeFormat> Formats { get; } =
    [
        new("openapi", "3.1", [NativeEncoding.Json, NativeEncoding.Yaml]),
        new("openapi", "3.0", [NativeEncoding.Json, NativeEncoding.Yaml]),
    ];

    public int CountEntries(ManifestNode tree) => Operations(tree).Count();

    public void Validate(ManifestNode tree, NativeContext context)
    {
        var is31 = context.Envelope.FormatVersion == "3.1";
        if (tree["openapi"] is { IsString: true, Text: { } version } openapi && !(is31 ? Line31() : Line30()).IsMatch(version))
        {
            context.Add(ManifestRules.NativeVersionMismatch, openapi.Pointer);
        }

        context.Schema(is31 ? SchemaIds.OpenApi31 : SchemaIds.OpenApi30, tree);
    }

    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) => ExtensionNames(tree);

    /// <summary>Header and cookie parameters, and header objects, with a credential name: their examples and defaults.</summary>
    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree) => CredentialHolders(tree).SelectMany(ExampleValues);

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) => [tree];

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
    {
        var info = tree["info"]!;
        var builder = new ProjectionBuilder(Id, Family, "openapi-api", envelope)
        {
            Attributes = new ProjectionAttributes { OpenapiVersion = tree.StringOf("openapi") },
        };
        foreach (var (kind, key, method, operation) in Operations(tree))
        {
            var operationId = operation.StringOf("operationId");
            builder.Entry(kind, operationId ?? $"{method.ToUpperInvariant()} {key}", operation.StringOf("summary"), nameDerived: operationId is null);
        }

        var title = info.StringOf("title")!;
        return builder.Build(title, title, info.StringOf("description"));
    }

    /// <summary>Path operations then webhook operations (3.1), in source order.</summary>
    private static IEnumerable<(string Kind, string Key, string Method, ManifestNode Operation)> Operations(ManifestNode tree)
    {
        foreach (var (kind, map) in new[] { ("operation", tree["paths"]), ("webhook", tree["webhooks"]) })
        {
            foreach (var (key, item) in Members(map))
            {
                foreach (var (method, operation) in Members(item).Where(m => Methods.Contains(m.Key, StringComparer.Ordinal) && m.Value.IsObject))
                {
                    yield return (kind, key, method, operation);
                }
            }
        }
    }

    /// <summary>Objects with <c>in: header|cookie</c> and a credential <c>name</c>, and members of <c>headers</c> maps with a credential name.</summary>
    internal static IEnumerable<ManifestNode> CredentialHolders(ManifestNode tree)
    {
        foreach (var node in tree.Descendants().Where(n => n.IsObject))
        {
            if (node.StringOf("in") is "header" or "cookie" && IsSecretHeader(node.StringOf("name")))
            {
                yield return node;
            }

            foreach (var (name, header) in Members(node["headers"]).Where(h => IsSecretHeader(h.Key) && h.Value.IsObject))
            {
                yield return header;
            }
        }
    }

    [GeneratedRegex(@"^3\.1\.\d+$", RegexOptions.ECMAScript)]
    private static partial Regex Line31();

    [GeneratedRegex(@"^3\.0\.\d+$", RegexOptions.ECMAScript)]
    private static partial Regex Line30();
}
