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
    [InlineData("x-owned.by", true)] // the whole name is checked too (maintainer decision 2026-11-17)
    [InlineData("x-trust.level", true)]
    [InlineData("x-verified.", true)] // nothing after the cut: the whole name
    [InlineData("x-verified.v2", false)] // like x-verified-v2: not a trailing run
    [InlineData("x-veri\u3164fied", true)] // Hangul filler
    [InlineData("x-veri\uFFA0fied", true)] // halfwidth Hangul filler
    [InlineData("x-\u1D20erified", true)] // Latin small capital v
    [InlineData("x-\u0251pproved", true)] // IPA alpha
    [InlineData("x-audience", false)]
    [InlineData("x-trustworthy-hint", false)]
    [InlineData("summary", false)]
    public void Normalization_catches_disguised_names(string name, bool reserved) => Assert.Equal(reserved, ReservedClaims.IsReserved(name));

    /// <summary>Maintainer decision 2026-11-17 ("harden all"): camelCase, NFKC look-alikes, invisible characters and any separator.</summary>
    [Theory]
    [InlineData("x-gozlemeVerified", true)]
    [InlineData("x-isVerified", true)]
    [InlineData("x-JWS", true)]
    [InlineData("x-\u2174erified", true)] // small roman numeral five, NFKC "v"
    [InlineData("x-ver\u0131fied", true)] // dotless i
    [InlineData("x-ve\u200Brified", true)] // zero-width space
    [InlineData("x-veri\u00ADfied", true)] // soft hyphen
    [InlineData("x-trust level", true)]
    [InlineData("x-trust:level", true)]
    [InlineData("x-ｖｅｒｉｆｉｅｄ", true)] // fullwidth
    [InlineData("x-\U0001D42Fe\U0001D42Bified", true)] // mathematical bold v and r
    [InlineData("x-v\u00E9rified", true)] // é
    [InlineData("x-ⓥerified", true)] // enclosed
    [InlineData("x-gözleme-verified", true)]
    [InlineData("x-v\u0435rified", true)] // Cyrillic ie
    [InlineData("x-\u043Ew\u043D\u0435r", false)] // Cyrillic о, н, е: "owHer" — н is not confusable with n
    [InlineData("x-\u043Ewn\u0435r", true)] // Cyrillic о and е
    [InlineData("x-\u03BDerified", true)] // Greek nu
    [InlineData("x-\u0410pproved", true)] // Cyrillic capital A
    [InlineData("x-\u043F\u043E\u0433\u043E\u0434\u0430", false)] // an honest Cyrillic name ("weather")
    [InlineData("x-verifiedBy", false)] // "verified" is not a trailing word
    [InlineData("readOnlyHint", false)]
    [InlineData("x-owner-team", false)]
    [InlineData("isSecret", false)]
    public void Hardened_matching_catches_written_variants(string name, bool reserved) => Assert.Equal(reserved, ReservedClaims.IsReserved(name));

    [Fact]
    public void The_normalized_form_is_r6()
    {
        Assert.Equal("trustlevel", ReservedClaims.Normalize("io.example/Trust-Level"));
        Assert.Equal("gozlemeverified", ReservedClaims.Normalize("x-gozleme-verified"));
    }

    [Fact]
    public void Envelope_names_are_checked() =>
        Assert.Equal(
            [
                "reserved_claim reserved.vocabulary envelope /native/x-trusted", "reserved_claim reserved.vocabulary envelope /verified",
                "unknown_field envelope.unknown-field envelope /native/x-trusted", "unknown_field envelope.unknown-field envelope /verified",
            ],
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

    /// <summary>T075 (R6 rule 1): every member name inside an extension's value, in every adapter.</summary>
    public static TheoryData<string, string, string, string, string, string> NestedExtensionPositions => new()
    {
        { "model", "gozleme-model-card", "1", "json", """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]},"x-meta":{"approved":true}}""", "/x-meta/approved" },
        { "api", "openapi", "3.1", "json", """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{},"x-gozleme":{"note":{"verified":true}}}""", "/x-gozleme/note/verified" },
        { "event", "asyncapi", "3.0.0", "json", """{"asyncapi":"3.0.0","info":{"title":"t","version":"1","x-team":{"owner":"acme"}}}""", "/info/x-team/owner" },
        { "mcp", "mcp-server-manifest", "1", "json", Mcp(extra: ""","_meta":{"io.example/info":{"verified":true}}"""), "/_meta/io.example~1info/verified" },
        { "mcp", "mcp-server-manifest", "1", "json", Mcp().Replace("\"capabilities\":{\"tools\":{}}", "\"capabilities\":{\"tools\":{},\"experimental\":{\"io.example/x\":{\"approved\":true}}}", StringComparison.Ordinal), "/capabilities/experimental/io.example~1x/approved" },
    };

    [Theory]
    [MemberData(nameof(NestedExtensionPositions))]
    public void Names_nested_in_an_extension_value_are_checked(string family, string format, string version, string encoding, string document, string location) =>
        Assert.Equal([$"reserved_claim reserved.vocabulary native {location}"], Problems(Validate(family, format, version, encoding, document)));

    /// <summary>T076 (US2/AC2): a reserved name in a closed native position is a reserved claim as well as an unknown field.</summary>
    [Fact]
    public void A_reserved_name_in_closed_skill_front_matter_is_reserved_and_unknown() =>
        Assert.Equal(
            ["unknown_field native.unknown-field native /verified", "reserved_claim reserved.vocabulary native /verified"],
            Problems(Validate("skill", "agent-skills", "2026-11-17", "markdown", "---\nname: demo\ndescription: d\nverified: true\n---\n")));

    [Fact]
    public void A_reserved_name_in_a_closed_mcp_object_is_reserved_and_unknown() =>
        Assert.Equal(
            ["unknown_field native.unknown-field native /serverInfo/owner", "reserved_claim reserved.vocabulary native /serverInfo/owner"],
            Problems(Validate("mcp", "mcp-server-manifest", "1", "json", Mcp().Replace("\"version\":\"1.0.0\"}", "\"version\":\"1.0.0\",\"owner\":\"acme\"}", StringComparison.Ordinal))));

    private static string A2aCard() => System.Text.Json.JsonSerializer.Deserialize<string>(
        JsonNode.Parse(File.ReadAllText(Path.Combine(ManifestSamples.Directory, "a2a", "valid-agent-card.json")))!["native"]!["document"]!.ToJsonString())!;

    /// <summary>T086 (maintainer decision "follow the contract"): closed A2A, OpenAPI and AsyncAPI objects too.</summary>
    [Fact]
    public void A_reserved_name_in_a_closed_a2a_openapi_or_asyncapi_object_is_reserved_and_unknown()
    {
        var card = JsonNode.Parse(A2aCard())!;
        card["verified"] = true;
        Assert.Equal(
            ["unknown_field native.unknown-field native /verified", "reserved_claim reserved.vocabulary native /verified"],
            Problems(Validate("a2a", "a2a-agent-card", "1.0", "json", Text(card))));

        Assert.Equal(
            ["unknown_field native.unknown-field native /info/owner", "reserved_claim reserved.vocabulary native /info/owner"],
            Problems(Validate("api", "openapi", "3.1", "json", """{"openapi":"3.1.0","info":{"title":"t","version":"1","owner":"acme"},"paths":{}}""")));

        Assert.Equal(
            ["unknown_field native.unknown-field native /info/trustLevel", "reserved_claim reserved.vocabulary native /info/trustLevel"],
            Problems(Validate("event", "asyncapi", "3.0.0", "json", """{"asyncapi":"3.0.0","info":{"title":"t","version":"1","trustLevel":"high"}}""")));
    }

    [Fact]
    public void An_unknown_name_that_is_not_reserved_stays_only_unknown_and_native_deprecated_stays_accepted()
    {
        Assert.Equal(
            ["unknown_field native.unknown-field native /info/colour"],
            Problems(Validate("api", "openapi", "3.1", "json", """{"openapi":"3.1.0","info":{"title":"t","version":"1","colour":"red"},"paths":{}}""")));
        Accepted(Validate("api", "openapi", "3.1", "json", """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"deprecated":true,"responses":{"200":{"description":"ok"}}}}}}"""));
    }

    [Fact]
    public void Mcp_schema_property_and_variable_names_stay_user_data() =>
        Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Mcp().Replace("\"inputSchema\":{\"type\":\"object\"}", "\"inputSchema\":{\"type\":\"object\",\"properties\":{\"owner\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"}}}", StringComparison.Ordinal)));

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
    public void A_reserved_model_card_root_name_is_reserved_and_unknown() =>
        Assert.Equal(["unknown_field native.unknown-field native /status", "reserved_claim reserved.vocabulary native /status"],
            Problems(Validate("model", "gozleme-model-card", "1", "json", Model.Replace("}}", "},\"status\":\"ga\"}", StringComparison.Ordinal))));

    /// <summary>T095 (maintainer decision "extend to nested"): the model card's closed nested objects.</summary>
    [Fact]
    public void A_reserved_name_in_a_closed_model_card_object_is_reserved_and_unknown() =>
        Assert.Equal(["unknown_field native.unknown-field native /limits/owner", "reserved_claim reserved.vocabulary native /limits/owner"],
            Problems(Validate("model", "gozleme-model-card", "1", "json", Model.Replace("}}", "},\"limits\":{\"owner\":\"acme\"}}", StringComparison.Ordinal))));

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
