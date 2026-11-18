using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// T083 (maintainer decisions 2026-11-17, "skip by position" and "check them"): <c>example</c>, <c>examples</c>,
/// <c>default</c>, <c>enum</c> and <c>const</c> are sample data only where they hold sample data; an entry of a map
/// with one of those names, and an OpenAPI Example object's own members, are structure and are checked.
/// </summary>
public sealed class DataPositionTests
{
    private const string External = "https://elsewhere.example.test/a.json";

    private static string OpenApi(string responses = "\"200\":{\"description\":\"ok\"}", string components = "{}") =>
        "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"paths\":{\"/a\":{\"get\":{\"responses\":{" + responses + "}}}},\"components\":" + components + "}";

    private static string[] Api(string doc) => Problems(Validate("api", "openapi", "3.1", "json", doc));

    public static TheoryData<string, string> CheckedPositions => new()
    {
        // responses.default is a Response object, not a default value.
        { OpenApi("\"default\":{\"$ref\":\"" + External + "\"}"), "external_reference ref.external native /paths/~1a/get/responses/default/$ref" },
        { OpenApi("\"default\":{\"description\":\"d\",\"x-verified\":true}"), "reserved_claim reserved.vocabulary native /paths/~1a/get/responses/default/x-verified" },

        // An Example object's own members; a Reference object in an examples map.
        { OpenApi(components: "{\"examples\":{\"e\":{\"value\":1,\"x-verified\":true}}}"), "reserved_claim reserved.vocabulary native /components/examples/e/x-verified" },
        { OpenApi(components: "{\"examples\":{\"e\":{\"$ref\":\"" + External + "\"}}}"), "external_reference ref.external native /components/examples/e/$ref" },

        // Schemas, properties and parameters named after the data keys.
        { OpenApi(components: "{\"schemas\":{\"default\":{\"type\":\"object\",\"x-trust\":1}}}"), "reserved_claim reserved.vocabulary native /components/schemas/default/x-trust" },
        { OpenApi(components: "{\"schemas\":{\"example\":{\"$ref\":\"" + External + "\"}}}"), "external_reference ref.external native /components/schemas/example/$ref" },
        { OpenApi(components: "{\"schemas\":{\"s\":{\"type\":\"object\",\"properties\":{\"enum\":{\"$ref\":\"" + External + "\"}}}}}"), "external_reference ref.external native /components/schemas/s/properties/enum/$ref" },
        { OpenApi(components: "{\"parameters\":{\"enum\":{\"name\":\"q\",\"in\":\"query\",\"schema\":{\"type\":\"string\"},\"x-verified\":true}}}"), "reserved_claim reserved.vocabulary native /components/parameters/enum/x-verified" },
    };

    [Theory]
    [MemberData(nameof(CheckedPositions))]
    public void A_structural_position_named_like_a_data_key_is_checked(string doc, string expected) => Assert.Contains(expected, Api(doc));

    public static TheoryData<string> DataPositionsStayData => new()
    {
        // Schema keyword values and an Example object's value are sample data.
        OpenApi(components: "{\"schemas\":{\"s\":{\"type\":\"object\",\"default\":{\"x-verified\":true},\"example\":{\"$ref\":\"" + External + "\"}}}}"),
        OpenApi(components: "{\"schemas\":{\"s\":{\"enum\":[{\"x-verified\":true}],\"const\":{\"$ref\":\"" + External + "\"},\"examples\":[{\"x-trust\":1}]}}}"),
        OpenApi(components: "{\"examples\":{\"e\":{\"value\":{\"x-verified\":true,\"$ref\":\"" + External + "\"}}}}"),
        OpenApi(components: "{\"parameters\":{\"p\":{\"name\":\"q\",\"in\":\"query\",\"schema\":{\"type\":\"string\"},\"example\":{\"x-verified\":true}}}}"),
    };

    [Theory]
    [MemberData(nameof(DataPositionsStayData))]
    public void Sample_data_is_not_checked(string doc) => Accepted(Validate("api", "openapi", "3.1", "json", doc));

    [Fact]
    public void An_asyncapi_message_example_is_data_but_a_message_named_default_is_checked()
    {
        const string Head = "{\"asyncapi\":\"3.0.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"components\":{\"messages\":{";
        Accepted(Validate("event", "asyncapi", "3.0.0", "json", Head + "\"m\":{\"examples\":[{\"payload\":{\"x-verified\":true}}]}}}}"));
        Assert.Contains(
            "reserved_claim reserved.vocabulary native /components/messages/default/x-verified",
            Problems(Validate("event", "asyncapi", "3.0.0", "json", Head + "\"default\":{\"x-verified\":true}}}}")));
    }
}
