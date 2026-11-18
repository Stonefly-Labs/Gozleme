using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 User Story 3 (SC-003): the native document is returned unchanged and digested as submitted; the
/// projection reads typed values only, so extension members and key order never change it and never appear in it.
/// </summary>
public sealed class ProjectionBoundaryTests
{
    private const string Marker = "ZZEXTENSIONMARKERZZ";

    private static (JsonNode Result, string Document) Run(ManifestSamples.Sample sample, string document)
    {
        var envelope = sample.Envelope(CanonicalId);
        envelope["native"]!["document"] = document;
        return (JsonNode.Parse(Json(Accepted(Validate(Encoding.UTF8.GetBytes(envelope.ToJsonString())))))!, document);
    }

    private static string JsonEdit(string document, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(document)!.AsObject();
        edit(node);
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Reversed(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in source.Reverse().ToList())
        {
            copy[name] = value?.DeepClone();
        }

        return copy;
    }

    /// <summary>Per family: a copy whose only change is an extension member, and a copy whose only change is key order.</summary>
    public static TheoryData<string> Families => ["valid-stateless-2026-07-28", "valid-agent-card", "valid-openapi-3.1", "valid-openapi-3.0", "valid-asyncapi-3.0.0", "valid-skill", "valid-model-card"];

    private static string WithExtension(string id, string document) => id switch
    {
        "valid-stateless-2026-07-28" => JsonEdit(document, d =>
        {
            d["_meta"]!["io.example.demo/build"] = Marker;
            d["tools"]![0]!["_meta"]!["io.example.demo/extra"] = Marker;
        }),
        "valid-agent-card" => JsonEdit(document, d => d["capabilities"]!["extensions"]![0]!["params"]!["marker"] = Marker),
        "valid-openapi-3.1" => document.Replace("x-audience: internal", "x-audience: " + Marker + "\n  x-added: " + Marker, StringComparison.Ordinal),
        "valid-openapi-3.0" or "valid-asyncapi-3.0.0" => JsonEdit(document, d => d["info"]!["x-audience"] = Marker),
        "valid-skill" => document.Replace("author-team: demo", "author-team: " + Marker + "\n  added: " + Marker, StringComparison.Ordinal),
        _ => JsonEdit(document, d =>
        {
            d["x-benchmark"] = Marker;
            d["modalities"]!["x-note"] = Marker;
        }),
    };

    private static string? Reordered(string id, string document) => id switch
    {
        "valid-stateless-2026-07-28" => JsonEdit(document, d => d["serverInfo"] = Reversed(d["serverInfo"]!.AsObject())),
        "valid-agent-card" => JsonEdit(document, d => d["capabilities"] = Reversed(d["capabilities"]!.AsObject())),
        "valid-openapi-3.0" or "valid-asyncapi-3.0.0" => JsonEdit(document, d => d["info"] = Reversed(d["info"]!.AsObject())),
        "valid-model-card" => JsonEdit(document, d => d["limits"] = Reversed(d["limits"]!.AsObject())),
        _ => null, // YAML and front matter: covered by the extension edit
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Extensions_change_the_digest_and_never_the_projection(string id)
    {
        var sample = ManifestSamples.Get(id);
        var (original, _) = Run(sample, sample.Document);
        var edited = WithExtension(id, sample.Document);
        Assert.NotEqual(sample.Document, edited);
        var (changed, _) = Run(sample, edited);

        Assert.NotEqual(original["native"]!["digest"]!.GetValue<string>(), changed["native"]!["digest"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original["projection"], changed["projection"]));
        Assert.DoesNotContain(Marker, changed["projection"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, changed["envelope"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(edited, changed["native"]!["document"]!.GetValue<string>());

        if (Reordered(id, sample.Document) is { } reordered)
        {
            var (moved, _) = Run(sample, reordered);
            Assert.NotEqual(original["native"]!["digest"]!.GetValue<string>(), moved["native"]!["digest"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(original["projection"], moved["projection"]));
        }
    }

    [Fact]
    public void The_document_is_returned_byte_identical_including_a_bom()
    {
        var sample = ManifestSamples.Get("valid-model-card");
        var document = "﻿" + sample.Document.Replace("\n", "\r\n", StringComparison.Ordinal);
        var (result, _) = Run(sample, document);
        Assert.Equal(document, result["native"]!["document"]!.GetValue<string>());
        var bytes = Encoding.UTF8.GetBytes(document);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), result["native"]!["digest"]!.GetValue<string>());
        Assert.Equal(bytes.Length, result["native"]!["byteLength"]!.GetValue<int>());
    }

    [Fact]
    public void Defaulted_values_are_listed_in_derived_and_the_document_is_unchanged()
    {
        var sample = ManifestSamples.Get("valid-openapi-3.1");
        var (result, _) = Run(sample, sample.Document);
        var projection = result["projection"]!;
        var derived = projection["derived"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        var post = projection["entries"]!.AsArray().Select((e, i) => (e, i)).Single(x => x.e!["nativeName"]!.GetValue<string>().StartsWith("post ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"/entries/{post.i}/nativeName", derived);
        Assert.DoesNotContain("operationId: post", sample.Document, StringComparison.Ordinal);
        Assert.Equal(sample.Document, result["native"]!["document"]!.GetValue<string>());
    }

    private static JsonNode Projection(string family, string format, string version, string document) =>
        JsonNode.Parse(Json(Accepted(Validate(family, format, version, "json", document))))!["projection"]!;

    private static string[] EntryNames(JsonNode projection) =>
        [.. projection["entries"]!.AsArray().Select(e => $"{e!["kind"]} {e["nativeName"]}")];

    /// <summary>T087 (maintainer decision "spec wins: sort", SC-003, US3/AC2): map key order never changes the projection.</summary>
    [Fact]
    public void Reordering_map_keys_never_changes_the_projection()
    {
        const string Head = "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},\"paths\":";
        const string Ok = "{\"responses\":{\"200\":{\"description\":\"ok\"}}}";
        var forward = Projection("api", "openapi", "3.1", Head + "{\"/a\":{\"get\":" + Ok + ",\"post\":" + Ok + "},\"/b\":{\"get\":" + Ok + "}}}");
        var backward = Projection("api", "openapi", "3.1", Head + "{\"/b\":{\"get\":" + Ok + "},\"/a\":{\"post\":" + Ok + ",\"get\":" + Ok + "}}}");
        Assert.Equal(["operation GET /a", "operation POST /a", "operation GET /b"], EntryNames(forward));
        Assert.True(JsonNode.DeepEquals(forward, backward));

        const string Event = "{\"asyncapi\":\"3.0.0\",\"info\":{\"title\":\"t\",\"version\":\"1\"},";
        var events = Projection("event", "asyncapi", "3.0.0", Event + "\"channels\":{\"a\":{},\"b\":{}},\"operations\":{\"x\":{\"action\":\"send\",\"channel\":{\"$ref\":\"#/channels/a\"}},\"y\":{\"action\":\"receive\",\"channel\":{\"$ref\":\"#/channels/b\"}}}}");
        var reversed = Projection("event", "asyncapi", "3.0.0", Event + "\"operations\":{\"y\":{\"action\":\"receive\",\"channel\":{\"$ref\":\"#/channels/b\"}},\"x\":{\"action\":\"send\",\"channel\":{\"$ref\":\"#/channels/a\"}}},\"channels\":{\"b\":{},\"a\":{}}}");
        Assert.Equal(["channel a", "channel b", "operation x", "operation y"], EntryNames(events));
        Assert.True(JsonNode.DeepEquals(events, reversed));
    }
}
