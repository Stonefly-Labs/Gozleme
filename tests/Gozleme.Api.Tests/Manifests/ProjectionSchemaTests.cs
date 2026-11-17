using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.Manifests;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 contracts/projection-v1.md: every projection of every valid sample validates against the closed
/// <c>projection-1.json</c>; the projection version is its own; no child identity field exists yet (GOZ-0008).
/// </summary>
public sealed class ProjectionSchemaTests
{
    public static TheoryData<string> Valid => [.. ManifestSamples.Valid.Select(s => s.Id)];

    private static JsonNode Projection(string id) =>
        JsonNode.Parse(Json(Accepted(Validate(ManifestSamples.Get(id).Body(CanonicalId)))))!["projection"]!;

    [Theory]
    [MemberData(nameof(Valid))]
    public void Every_projection_validates_against_the_closed_schema(string id)
    {
        var projection = Projection(id);
        Assert.Empty(Schemas.Validate(SchemaIds.Projection, Encoding.UTF8.GetBytes(projection.ToJsonString()), ManifestPart.Native));
        Assert.Equal("1", projection["projectionVersion"]!.GetValue<string>());
        foreach (var entry in projection["entries"]!.AsArray())
        {
            Assert.Equal(["kind", "nativeName"], entry!.AsObject().Select(p => p.Key).Where(k => k != "title")); // no child id (GOZ-0008)
        }
    }

    [Fact]
    public void The_projection_version_is_independent_of_the_other_versions()
    {
        var sample = ManifestSamples.Get("valid-model-card");
        var result = JsonNode.Parse(Json(Accepted(Validate(sample.Body(CanonicalId)))))!;
        Assert.Equal("1", result["projection"]!["projectionVersion"]!.GetValue<string>());
        Assert.Equal("1.4.0", result["projection"]!["declaredVersion"]!.GetValue<string>());
        Assert.Equal(1, result["envelope"]!["envelopeVersion"]!.GetValue<int>());
        Assert.Equal("1", result["native"]!["formatVersion"]!.GetValue<string>());

        var openapi = JsonNode.Parse(Json(Accepted(Validate(ManifestSamples.Get("valid-openapi-3.0").Body(CanonicalId)))))!;
        Assert.Equal(("3.0", "1"), (openapi["native"]!["formatVersion"]!.GetValue<string>(), openapi["projection"]!["projectionVersion"]!.GetValue<string>()));
    }

    [Fact]
    public void An_extra_projection_member_fails_the_closed_schema()
    {
        var projection = Projection("valid-model-card").AsObject();
        projection["childId"] = "x";
        Assert.NotEmpty(Schemas.Validate(SchemaIds.Projection, Encoding.UTF8.GetBytes(projection.ToJsonString()), ManifestPart.Native));
    }
}
