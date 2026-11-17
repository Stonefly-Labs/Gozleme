using System.Text.Json.Nodes;
using Gozleme.Api.Manifests.Rules;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 research R6 (FR-007, FR-009): the reserved vocabulary in name positions (envelope names and each format's
/// extension mechanism) and the adapter-listed fields; never in user data, example values or native
/// <c>deprecated</c> flags (agent choice A1).
/// </summary>
public sealed class ReservedClaimTests
{
    private const string Model = """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]}}""";

    [Theory]
    [InlineData("x-gozleme-verified", true)]
    [InlineData("io.example/trust-level", true)]
    [InlineData("Approved", true)]
    [InlineData("x-Signature", true)]
    [InlineData("delegated_by", true)]
    [InlineData("x-owned.by", false)] // the namespace cut keeps "by" only
    [InlineData("x-audience", false)]
    [InlineData("x-trustworthy-hint", false)]
    [InlineData("summary", false)]
    public void Normalization_catches_disguised_names(string name, bool reserved) => Assert.Equal(reserved, ReservedClaims.IsReserved(name));

    [Fact]
    public void The_normalized_form_is_r6()
    {
        Assert.Equal("trustlevel", ReservedClaims.Normalize("io.example/Trust-Level"));
        Assert.Equal("gozlemeverified", ReservedClaims.Normalize("x-gozleme-verified"));
    }

    [Fact]
    public void Envelope_names_are_checked() =>
        Assert.Equal(
            ["reserved_claim reserved.vocabulary envelope /native/x-trusted", "reserved_claim reserved.vocabulary envelope /verified"],
            Problems(Validate("model", "gozleme-model-card", "1", "json", Model, e =>
            {
                e["verified"] = true;
                e["native"]!["x-trusted"] = 1;
            })).Order(StringComparer.Ordinal));

    public static TheoryData<string, string, string, string, string, string> ExtensionPositions => new()
    {
        { "model", "gozleme-model-card", "1", "json", """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"],"x-approved":1}}""", "/modalities/x-approved" },
        { "api", "openapi", "3.0", "json", """{"openapi":"3.0.4","info":{"title":"t","version":"1","x-gozleme-verified":true},"paths":{}}""", "/info/x-gozleme-verified" },
        { "event", "asyncapi", "3.0.0", "json", """{"asyncapi":"3.0.0","info":{"title":"t","version":"1","x-status":"ga"}}""", "/info/x-status" },
        { "skill", "agent-skills", "2026-11-17", "markdown", "---\nname: demo\ndescription: d\nmetadata:\n  trust-level: high\n---\n", "/metadata/trust-level" },
        { "mcp", "mcp-server-manifest", "1", "json", Mcp(extra: ""","_meta":{"io.example/provenance":"x"}"""), "/_meta/io.example~1provenance" },
    };

    [Theory]
    [MemberData(nameof(ExtensionPositions))]
    public void Every_extension_position_is_checked(string family, string format, string version, string encoding, string document, string location) =>
        Assert.Equal([$"reserved_claim reserved.vocabulary native {location}"], Problems(Validate(family, format, version, encoding, document)));

    [Fact]
    public void A2a_extension_params_names_and_signatures_are_checked()
    {
        var card = JsonNode.Parse(System.Text.Json.JsonSerializer.Deserialize<string>(
            JsonNode.Parse(File.ReadAllText(Path.Combine(ManifestSamples.Directory, "a2a", "valid-agent-card.json")))!["native"]!["document"]!.ToJsonString())!)!;
        card["capabilities"]!["extensions"]![0]!["params"]!["publishedBy"] = "x";
        Assert.Equal(["reserved_claim reserved.vocabulary native /capabilities/extensions/0/params/publishedBy"], Problems(Validate("a2a", "a2a-agent-card", "1.0", "json", Text(card))));
        card["capabilities"]!["extensions"]![0]!["params"]!.AsObject().Remove("publishedBy");
        card["signatures"] = new JsonArray(new JsonObject { ["protected"] = "eyJ", ["signature"] = "SYNTHETIC" });
        Assert.Equal(["reserved_claim reserved.a2a-signatures native /signatures"], Problems(Validate("a2a", "a2a-agent-card", "1.0", "json", Text(card))));
    }

    [Fact]
    public void A_reserved_model_card_root_name_is_reserved_not_unknown() =>
        Assert.Equal(["reserved_claim reserved.vocabulary native /status"],
            Problems(Validate("model", "gozleme-model-card", "1", "json", Model.Replace("}}", "},\"status\":\"ga\"}", StringComparison.Ordinal))));

    [Fact]
    public void User_data_examples_and_native_deprecated_flags_are_not_checked()
    {
        const string api = """
            {"openapi":"3.0.4","info":{"title":"t","version":"1"},
             "paths":{"/a":{"get":{"deprecated":true,"parameters":[{"name":"owner","in":"query","deprecated":true,"schema":{"type":"string"},"example":{"verified":true}}],
               "responses":{"200":{"description":"ok"}}}}},
             "components":{"schemas":{"Repo":{"type":"object","deprecated":true,"properties":{"owner":{"type":"string"},"status":{"type":"string"}}}}}}
            """;
        Accepted(Validate("api", "openapi", "3.0", "json", api));
    }
}
