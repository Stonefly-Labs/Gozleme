using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using Gozleme.Api.Manifests.Rules;
using static Gozleme.Api.Manifests.Adapters.AdapterTree;

namespace Gozleme.Api.Manifests.Adapters;

/// <summary>
/// <c>event/1</c>: AsyncAPI 3.0.0 (<c>spec-json-schemas</c> v6.11.1). <c>asyncapi</c> must equal <c>3.0.0</c>;
/// <c>^x-</c> members are the extension mechanism; reserved, secret and reference rules as for <c>api/1</c>, with
/// message and message-trait <c>headers</c> schemas checked for the credential header names.
/// </summary>
internal sealed class AsyncApiAdapter : IProtocolAdapter
{
    public string Id => "event/1";

    public string Family => "event";

    public IReadOnlyList<NativeFormat> Formats { get; } = [new("asyncapi", "3.0.0", [NativeEncoding.Json, NativeEncoding.Yaml])];

    public int CountEntries(ManifestNode tree) => Count(tree["channels"]) + Count(tree["operations"]);

    public void Validate(ManifestNode tree, NativeContext context)
    {
        if (tree["asyncapi"] is { IsString: true, Text: not "3.0.0" } version)
        {
            context.Add(ManifestRules.NativeVersionMismatch, version.Pointer);
        }

        context.Schema(SchemaIds.AsyncApi300, tree);
    }

    public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree) => ExtensionNames(tree, DataPositions.AsyncApi);

    /// <summary>The <c>default</c>, <c>example</c> and <c>examples</c> of credential-named properties of any <c>headers</c> schema.</summary>
    public IEnumerable<ManifestNode> SecretFields(ManifestNode tree) =>
        tree.Descendants().Where(n => n.IsObject).Select(n => n["headers"]?["properties"]).OfType<ManifestNode>()
            .SelectMany(p => Members(p).Where(m => IsSecretHeader(m.Key) && m.Value.IsObject))
            .SelectMany(m => ExampleValues(m.Value));

    public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree) => [tree];

    public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
    {
        var info = tree["info"]!;
        var builder = new ProjectionBuilder(Id, Family, "asyncapi-application", envelope)
        {
            Attributes = new ProjectionAttributes { AsyncapiVersion = tree.StringOf("asyncapi") },
        };
        foreach (var (key, channel) in Members(tree["channels"]))
        {
            builder.Entry("channel", key, channel.StringOf("title"));
        }

        foreach (var (key, operation) in Members(tree["operations"]))
        {
            builder.Entry("operation", key, operation.StringOf("title"));
        }

        var title = info.StringOf("title")!;
        return builder.Build(title, title, info.StringOf("description"));
    }
}
