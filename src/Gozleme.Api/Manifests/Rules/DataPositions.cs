using System.Collections.Frozen;
using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>What an object is, as far as telling structure from sample data goes.</summary>
internal enum PositionKind
{
    /// <summary>An ordinary object: <c>example</c>, <c>default</c>, <c>enum</c>, <c>const</c> and an array <c>examples</c> are sample data.</summary>
    Object,

    /// <summary>A map whose entries are named by the author (<c>responses</c>, <c>components.schemas</c>): every entry is structure.</summary>
    Map,

    /// <summary>A map whose entry names are user data (schema <c>properties</c>): entries are structure, names are not extension names.</summary>
    DataNameMap,

    /// <summary>An OpenAPI <c>examples</c> map: each entry is an Example object.</summary>
    ExampleMap,

    /// <summary>An OpenAPI Example object: only its <c>value</c> is sample data.</summary>
    ExampleObject,

    /// <summary>A map whose entries are maps (OpenAPI <c>callbacks</c>: each Callback object maps expressions to Path Items; T093).</summary>
    MapOfMaps,

    /// <summary>An OpenAPI <c>links</c> map: each entry is a Link object.</summary>
    LinkMap,

    /// <summary>An OpenAPI Link object: its <c>requestBody</c> and <c>parameters</c> values are literal data (T094).</summary>
    LinkObject,
}

/// <summary>
/// Skips sample data by position, not by name (maintainer decisions 2026-11-17, "skip by position" and "check them",
/// T083): <c>example</c>, <c>default</c>, <c>enum</c>, <c>const</c> and an array-valued <c>examples</c> are data only
/// as fields of an ordinary object, and an Example object's <c>value</c> is data. An entry of a map with one of those
/// names (an OpenAPI <c>responses.default</c>, a schema or parameter named <c>enum</c>) is structure and is checked.
/// </summary>
internal sealed class DataPositions
{
    private static readonly string[] SchemaMaps = ["$defs", "definitions", "dependentSchemas", "dependencies"];

    private static readonly string[] DataNameMaps = ["properties", "patternProperties"];

    private static readonly string[] DataFields = ["example", "default", "enum", "const"];

    /// <summary>OpenAPI 3.0/3.1: Paths, Responses, Callbacks, Components maps and the maps inside operations and media types.</summary>
    public static readonly DataPositions OpenApi = new(
    [
        "paths", "webhooks", "responses", "schemas", "parameters", "headers", "content", "requestBodies",
        "securitySchemes", "pathItems", "encoding", "variables", "mapping", "scopes", .. SchemaMaps,
    ], openApiMaps: true, objectExamples: true);

    /// <summary>AsyncAPI 3.0: message <c>headers</c> is a schema here, not a map.</summary>
    public static readonly DataPositions AsyncApi = new(
    [
        "channels", "operations", "messages", "servers", "variables", "parameters", "schemas", "securitySchemes", "serverVariables",
        "messageTraits", "operationTraits", "correlationIds", "replies", "replyAddresses", "externalDocs", "tags", "serverBindings",
        "channelBindings", "operationBindings", "messageBindings", "scopes", .. SchemaMaps,
    ], openApiMaps: false, objectExamples: true);

    /// <summary>
    /// A bare JSON Schema (MCP <c>inputSchema</c>/<c>outputSchema</c>): <c>examples</c> is data whatever its type
    /// (maintainer decision 2026-11-17, "treat as data", T094).
    /// </summary>
    public static readonly DataPositions JsonSchema = new([.. SchemaMaps], openApiMaps: false, objectExamples: false);

    private readonly FrozenSet<string> _maps;

    private readonly bool _openApiMaps;

    private readonly bool _objectExamples;

    private DataPositions(string[] maps, bool openApiMaps, bool objectExamples)
    {
        _maps = maps.ToFrozenSet(StringComparer.Ordinal);
        _openApiMaps = openApiMaps;
        _objectExamples = objectExamples;
    }

    /// <summary>The structural members of <paramref name="node"/> (of kind <paramref name="kind"/>) with each value's kind; sample data is left out.</summary>
    public IEnumerable<(string Name, ManifestNode Value, PositionKind Kind)> Members(ManifestNode node, PositionKind kind)
    {
        foreach (var (name, value) in node.Members)
        {
            switch (kind)
            {
                case PositionKind.Map or PositionKind.DataNameMap:
                    yield return (name, value, PositionKind.Object);
                    continue;
                case PositionKind.ExampleMap:
                    yield return (name, value, PositionKind.ExampleObject);
                    continue;
                case PositionKind.MapOfMaps:
                    yield return (name, value, PositionKind.Map);
                    continue;
                case PositionKind.LinkMap:
                    yield return (name, value, PositionKind.LinkObject);
                    continue;
                case PositionKind.ExampleObject when name == "value":
                case PositionKind.LinkObject when name is "requestBody" or "parameters":
                    continue;
            }

            if (DataFields.Contains(name, StringComparer.Ordinal) || (name == "examples" && (!value.IsObject || !_objectExamples)))
            {
                continue;
            }

            yield return (name, value, !value.IsObject ? PositionKind.Object
                : _openApiMaps && name == "callbacks" ? PositionKind.MapOfMaps
                : _openApiMaps && name == "links" ? PositionKind.LinkMap
                : name == "examples" ? PositionKind.ExampleMap
                : DataNameMaps.Contains(name, StringComparer.Ordinal) ? PositionKind.DataNameMap
                : _maps.Contains(name) ? PositionKind.Map
                : PositionKind.Object);
        }
    }
}
