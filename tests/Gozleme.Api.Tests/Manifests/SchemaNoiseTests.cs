using System.Text.Json.Nodes;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// Maintainer decision 2026-11-17 ("separate real from false"): an invalid value never hides a real unknown member
/// beside it, and the members its failed subschema declares are not reported as unknown.
/// </summary>
public sealed class SchemaNoiseTests
{
    [Fact]
    public void An_invalid_mcp_tool_value_reports_the_real_unknown_member_beside_it_and_no_declared_one()
    {
        var doc = JsonNode.Parse(Mcp())!.AsObject();
        doc["tools"]![0]!["title"] = 5;
        doc["tools"]![0]!["bogus"] = 1;

        Assert.Equal(
            ["unknown_field native.unknown-field native /tools/0/bogus", "invalid_value native.schema native /tools/0/title"],
            Problems(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc))));
    }

    [Fact]
    public void An_invalid_openapi_value_reports_the_real_unknown_member_and_keeps_extensions_open()
    {
        const string doc = """{"openapi":"3.1.0","info":{"title":5,"version":"1.0.0","bogus":1,"x-team":"a"},"paths":{}}""";

        Assert.Equal(
            ["unknown_field native.unknown-field native /info/bogus", "invalid_value native.schema native /info/title"],
            Problems(Validate("api", "openapi", "3.1", "json", doc)));
    }

    /// <summary>
    /// A stdio transport declares only <c>type</c>; <c>url</c> is declared by the HTTP alternatives, which did not match.
    /// Only matched alternatives count, so the stray member is reported, not hidden as declared elsewhere.
    /// </summary>
    [Fact]
    public void A_member_declared_only_by_an_alternative_that_did_not_match_is_unknown()
    {
        var doc = JsonNode.Parse(Mcp())!.AsObject();
        doc["packages"] = new JsonArray(new JsonObject
        {
            ["registryType"] = "npm",
            ["identifier"] = "@example/forecasts",
            ["version"] = 5,
            ["transport"] = new JsonObject { ["type"] = "stdio", ["url"] = "https://forecasts.example.test/mcp" },
        });

        // The invalid version keeps the result non-empty, so the fallback for an all-noise result cannot mask a hidden url.
        Assert.Equal(
            ["unknown_field native.unknown-field native /packages/0/transport/url", "invalid_value native.schema native /packages/0/version"],
            Problems(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc))));
    }

    /// <summary>
    /// T073 (SC-006, security review F1): thousands of unknown members on one object beside an invalid value cost one
    /// branch re-validation per alternative, not one per member, and every one is still reported (up to the cap).
    /// </summary>
    [Fact]
    public void Many_unknown_members_beside_an_invalid_value_stay_cheap()
    {
        var doc = JsonNode.Parse(Mcp())!.AsObject();
        var transport = new JsonObject { ["type"] = "stdio" };
        for (var i = 0; i < 8_000; i++)
        {
            transport["u" + i] = 1;
        }

        doc["packages"] = new JsonArray(new JsonObject { ["registryType"] = "npm", ["identifier"] = "@e/f", ["version"] = 5, ["transport"] = transport });
        var text = Text(doc);
        Validate("mcp", "mcp-server-manifest", "1", "json", text); // warm the branch wrappers
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Assert.IsType<Gozleme.Api.Manifests.RejectedResult>(Validate("mcp", "mcp-server-manifest", "1", "json", text));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.True(result.Truncated);
        Assert.All(result.Problems, p => Assert.Contains(p.Category, (string[])["unknown_field", "invalid_value"]));
    }
}
