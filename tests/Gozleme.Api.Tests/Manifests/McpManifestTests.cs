using System.Text.Json.Nodes;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 adapter <c>mcp/1</c> (contracts/native-formats.md, maintainer P4): primitives are typed by the declared
/// <c>protocolRevision</c>, <c>mode</c> is derived from it, and the consistency rules hold.
/// </summary>
public sealed class McpManifestTests
{
    private static JsonObject Doc(string revision = "2026-07-28") => JsonNode.Parse(Mcp(revision))!.AsObject();

    private static string[] Run(JsonObject doc) => Problems(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc)));

    private static JsonObject Projection(JsonObject doc) =>
        JsonNode.Parse(Json(Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc)))))!["projection"]!.AsObject();

    [Fact]
    public void A_primitive_is_typed_by_its_declared_revision()
    {
        var stateless = Doc();
        stateless["tools"]![0]!["execution"] = new JsonObject { ["taskSupport"] = "optional" };
        Assert.Equal(["unknown_field native.unknown-field native /tools/0/execution"], Run(stateless));

        var stateful = Doc("2025-11-25");
        stateful["tools"]![0]!["execution"] = new JsonObject { ["taskSupport"] = "optional" };
        Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Text(stateful)));
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-29")]
    [InlineData("")]
    public void Another_revision_is_an_unsupported_format(string revision) =>
        Assert.Equal(["unsupported_format native.version-mismatch native /protocolRevision", "invalid_value native.schema native /protocolRevision"], Run(Doc(revision)));

    [Theory]
    [InlineData("2026-07-28", "stateless")]
    [InlineData("2025-11-25", "stateful")]
    public void Mode_is_derived_and_listed(string revision, string mode)
    {
        var projection = Projection(Doc(revision));
        Assert.Equal(mode, projection["attributes"]!["mode"]!.GetValue<string>());
        Assert.Contains("/attributes/mode", projection["derived"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal("mcp-server", projection["kind"]!.GetValue<string>());
        Assert.Equal("io.example.demo/forecasts", projection["nativeName"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_need_the_tools_capability()
    {
        var doc = Doc();
        doc["capabilities"] = new JsonObject();
        Assert.Equal(["invalid_value native.consistency native /tools"], Run(doc));
        doc["tools"] = new JsonArray();
        Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc)));
    }

    [Fact]
    public void Resources_and_templates_need_the_resources_capability()
    {
        var doc = Doc();
        doc["resources"] = new JsonArray(new JsonObject { ["name"] = "r", ["uri"] = "https://forecasts.example.test/r" });
        doc["resourceTemplates"] = new JsonArray(new JsonObject { ["name"] = "t", ["uriTemplate"] = "https://forecasts.example.test/{id}" });
        Assert.Equal(["invalid_value native.consistency native /resourceTemplates", "invalid_value native.consistency native /resources"], Run(doc));
    }

    [Fact]
    public void Prompts_need_the_prompts_capability()
    {
        var doc = Doc();
        doc["prompts"] = new JsonArray(new JsonObject { ["name"] = "p" });
        Assert.Equal(["invalid_value native.consistency native /prompts"], Run(doc));
    }

    [Fact]
    public void Names_are_unique_per_list_and_resource_uris_are_unique()
    {
        var doc = Doc();
        doc["capabilities"]!["resources"] = new JsonObject();
        doc["tools"]!.AsArray().Add(new JsonObject { ["name"] = "get_forecast", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        doc["resources"] = new JsonArray(
            new JsonObject { ["name"] = "a", ["uri"] = "https://forecasts.example.test/r" },
            new JsonObject { ["name"] = "b", ["uri"] = "https://forecasts.example.test/r" });
        Assert.Equal(["invalid_value native.consistency native /resources/1/uri", "invalid_value native.consistency native /tools/1/name"], Run(doc));
    }

    [Theory]
    [InlineData("io.example.demo/cost", null)]
    [InlineData("cost", "invalid_value native.meta-key native /_meta/cost")]
    [InlineData("example/cost", "invalid_value native.meta-key native /_meta/example~1cost")]
    [InlineData("io.modelcontextprotocol/related", "reserved_claim reserved.mcp-namespace native /_meta/io.modelcontextprotocol~1related")]
    [InlineData("io.modelcontextprotocol.registry/official", "reserved_claim reserved.mcp-registry-meta native /_meta/io.modelcontextprotocol.registry~1official")]
    [InlineData("io.modelcontextprotocol.registry/publisher-provided", null)]
    public void Meta_keys_are_reverse_dns_and_upstream_namespaces_are_reserved(string key, string? expected)
    {
        var doc = Doc();
        doc["_meta"] = new JsonObject { [key] = new JsonObject() };
        if (expected is null)
        {
            Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc)));
        }
        else
        {
            Assert.Equal([expected], Run(doc));
        }
    }
}
