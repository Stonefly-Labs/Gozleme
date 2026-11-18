using System.Text.Json;
using System.Text.Json.Serialization;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Manifests;

/// <summary>Which part a location points into (contracts/manifest-validation.md).</summary>
internal static class ManifestPart
{
    public const string Envelope = "envelope";
    public const string Native = "native";
}

/// <summary>
/// A problem location: <c>{part, pointer}</c> (RFC 6901 into the part's tree) or <c>{part, line, column}</c> (1-based,
/// Unicode scalar values). A <c>null</c> location is used for <c>too_large</c>.
/// </summary>
[JsonConverter(typeof(ProblemLocationConverter))]
internal sealed record ProblemLocation(string Part, JsonPointer? Path, int Line, int Column)
{
    /// <summary>The RFC 6901 text of <see cref="Path"/>, built on demand (only reported problems are written).</summary>
    public string? Pointer => Path?.ToString();

    public static ProblemLocation At(string part, JsonPointer pointer) => new(part, pointer, 0, 0);

    public static ProblemLocation At(string part, string pointer) => new(part, JsonPointer.Parse(pointer), 0, 0);

    public static ProblemLocation At(string part, int line, int column) => new(part, null, line, column);
}

/// <summary>One problem as the pipeline holds it: a catalogue rule and a location; never any submitted value.</summary>
internal sealed record ManifestProblem(ManifestRule Rule, ProblemLocation? Location);

/// <summary>The wire form of a problem: <c>{category, rule, location, message}</c>, in that order.</summary>
internal sealed record ValidationProblem(string Category, string Rule, ProblemLocation? Location, string Message)
{
    public static ValidationProblem From(ManifestProblem problem) =>
        new(ManifestRules.Name(problem.Rule.Category), problem.Rule.Id, problem.Location, problem.Rule.Message);
}

/// <summary>The envelope echoed in an accepted result: every member except <c>native.document</c>.</summary>
internal sealed record EnvelopeSummary(
    int EnvelopeVersion,
    string Family,
    string Capability,
    string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary,
    NativeDeclaration Native);

internal sealed record NativeDeclaration(string Format, string FormatVersion, string Encoding);

/// <summary>The native document, unchanged, with the SHA-256 of its UTF-8 bytes (FR-013).</summary>
internal sealed record NativeResult(string Format, string FormatVersion, string Encoding, string Document, string Digest, int ByteLength);

/// <summary>Both outcomes answer 200 (agent choice A3); the endpoint serializes the concrete type.</summary>
internal abstract record ValidationResult([property: JsonPropertyOrder(-1)] string Outcome);

internal sealed record AcceptedResult(EnvelopeSummary Envelope, NativeResult Native, ManifestProjection Projection) : ValidationResult(ManifestOutcome.Accepted);

internal sealed record RejectedResult(IReadOnlyList<ValidationProblem> Problems, bool Truncated) : ValidationResult(ManifestOutcome.Rejected);

/// <summary>Error bodies for the non-200 answers (contracts/manifest-api.openapi.yaml).</summary>
internal sealed record ManifestError(string Error)
{
    public const string Forbidden = "forbidden";
    public const string Suspended = "suspended";
    public const string RateLimited = "rate_limited";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string TooLarge = "too_large";
    public const string Unavailable = "unavailable";
}

/// <summary>Bounded outcomes: the result's <c>outcome</c> and the telemetry label values (research R16).</summary>
internal static class ManifestOutcome
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Suspended = "suspended";
    public const string RateLimited = "rate_limited";
    public const string TooLarge = "too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// One serializer configuration for results: camel case, declaration order and no indentation, so identical input
/// gives byte-identical output (FR-015).
/// </summary>
internal static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>The projection as a JSON tree, the input of its canonical form (GOZ-0007 research R3).</summary>
    public static System.Text.Json.Nodes.JsonNode ProjectionNode(Projection.ManifestProjection projection) =>
        JsonSerializer.SerializeToNode(projection, Options)!;

    /// <summary>The envelope summary as stored on a revision (not signed).</summary>
    public static string SummaryJson(EnvelopeSummary summary) => JsonSerializer.Serialize(summary, Options);

    public static byte[] Serialize(ValidationResult result) => result switch
    {
        AcceptedResult accepted => JsonSerializer.SerializeToUtf8Bytes(accepted, Options),
        RejectedResult rejected => JsonSerializer.SerializeToUtf8Bytes(rejected, Options),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}

internal sealed class ProblemLocationConverter : JsonConverter<ProblemLocation>
{
    public override ProblemLocation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Problem locations are written only.");

    public override void Write(Utf8JsonWriter writer, ProblemLocation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("part"u8, value.Part);
        if (value.Pointer is { } pointer)
        {
            writer.WriteString("pointer"u8, pointer);
        }
        else
        {
            writer.WriteNumber("line"u8, value.Line);
            writer.WriteNumber("column"u8, value.Column);
        }

        writer.WriteEndObject();
    }
}
