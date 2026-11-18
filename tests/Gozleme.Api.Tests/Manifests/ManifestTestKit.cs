using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Adapters;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>Shared fixtures: one validator over the real bundled schemas and adapters, and envelope builders.</summary>
internal static class ManifestTestKit
{
    public const string CanonicalId = "gz://registry.demo.gozleme.test/cap/0193a000-0000-7000-8000-000000000011";
    public const string Alias = "seeded-demo/manifest-demo";

    public static readonly ManifestTarget Target = new(CanonicalId, Alias);

    public static readonly SchemaRegistry Schemas = new();

    public static readonly ManifestValidator Validator = new(Schemas, ManifestAdapters.All);

    public static readonly (string Family, string Format, string Version, string Encoding)[] Rows =
    [
        ("mcp", "mcp-server-manifest", "1", "json"), ("a2a", "a2a-agent-card", "1.0", "json"), ("api", "openapi", "3.1", "json"),
        ("event", "asyncapi", "3.0.0", "json"), ("skill", "agent-skills", "2026-11-17", "markdown"), ("model", "gozleme-model-card", "1", "json"),
    ];

    /// <summary>An envelope with the given native document; <paramref name="extra"/> adds or replaces top-level members.</summary>
    public static byte[] Envelope(string family, string format, string formatVersion, string encoding, string document, Action<JsonObject>? extra = null)
    {
        var envelope = new JsonObject
        {
            ["envelopeVersion"] = 1,
            ["family"] = family,
            ["capability"] = CanonicalId,
            ["version"] = "1.0.0",
            ["native"] = new JsonObject { ["format"] = format, ["formatVersion"] = formatVersion, ["encoding"] = encoding, ["document"] = document },
        };
        extra?.Invoke(envelope);
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    public static ValidationResult Validate(byte[] body) => Validator.Validate(body, Target).Result;

    public static ValidationResult Validate(string family, string format, string formatVersion, string encoding, string document, Action<JsonObject>? extra = null) =>
        Validate(Envelope(family, format, formatVersion, encoding, document, extra));

    /// <summary>"category rule part pointer|line:column" lines for a rejected result.</summary>
    public static string[] Problems(ValidationResult result) => result is RejectedResult rejected
        ? [.. rejected.Problems.Select(p => $"{p.Category} {p.Rule} {Where(p.Location)}")]
        : throw new Xunit.Sdk.XunitException($"Expected a rejection: {Json(result)}");

    public static string Where(ProblemLocation? location) => location switch
    {
        null => "-",
        { Pointer: { } pointer } => $"{location.Part} {pointer}",
        _ => $"{location.Part} {location.Line}:{location.Column}",
    };

    public static AcceptedResult Accepted(ValidationResult result) =>
        result as AcceptedResult ?? throw new Xunit.Sdk.XunitException($"Expected acceptance: {Json(result)}");

    public static string Json(ValidationResult result) => Encoding.UTF8.GetString(ManifestJson.Serialize(result));

    public static string Mcp(string revision = "2026-07-28", string extra = "") =>
        "{\"protocolRevision\":\"" + revision + "\",\"serverInfo\":{\"name\":\"io.example.demo/forecasts\",\"version\":\"1.0.0\"},"
        + "\"capabilities\":{\"tools\":{}},\"tools\":[{\"name\":\"get_forecast\",\"inputSchema\":{\"type\":\"object\"}}]" + extra + "}";

    public static string Text(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
