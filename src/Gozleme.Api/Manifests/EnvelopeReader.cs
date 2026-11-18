using System.Text;
using System.Text.RegularExpressions;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests;

/// <summary>The capability being validated against: its canonical ID and its alias <c>&lt;handle&gt;/&lt;name&gt;</c>.</summary>
internal sealed record ManifestTarget(string CanonicalId, string Alias);

/// <summary>A manifest envelope v1 that passed the stopping stages (contracts/manifest-envelope-v1.schema.json).</summary>
internal sealed record ManifestEnvelope(
    string Family,
    string? Capability,
    string? Version,
    string? Title,
    string? Summary,
    string Format,
    string FormatVersion,
    string Encoding,
    string Document);

/// <summary>
/// Envelope stages 1–3 (contracts/manifest-validation.md, research R3, R5, R8). Stage 1 parses the request body
/// (<c>malformed</c>, <c>too_large</c>); stage 2 checks, in order and each stopping, the envelope version, the
/// family, the format row and the encoding, then that <c>native.document</c> is a string; stage 3 collects unknown
/// members, required members, SemVer, lengths and the identity claim. Reserved names and secrets in the envelope
/// are collected by <see cref="ManifestValidator"/> over the returned tree.
/// </summary>
internal static partial class EnvelopeReader
{
    public const int MaxEnvelopeBytes = 16 * 1024;
    public const int MaxCapabilityLength = 512;
    public const int MaxVersionLength = 256;
    public const int MaxTitleLength = 100;
    public const int MaxSummaryLength = 500;

    private const string DocumentPointer = "/native/document";

    private static readonly string[] RootMembers = ["envelopeVersion", "family", "capability", "version", "title", "summary", "native"];
    private static readonly string[] NativeMembers = ["format", "formatVersion", "encoding", "document"];

    /// <summary>Reads the envelope; throws <see cref="ValidationStop"/> for a stopping stage and collects the rest into <paramref name="problems"/>.</summary>
    public static (ManifestEnvelope Envelope, ManifestNode Tree) Read(
        ReadOnlyMemory<byte> body, ManifestTarget target, IReadOnlyDictionary<string, IProtocolAdapter> adapters, List<ManifestProblem> problems)
    {
        var tree = JsonTreeReader.Read(body, ManifestPart.Envelope, DocumentPointer, out var documentBytes);
        if (body.Length - documentBytes > MaxEnvelopeBytes)
        {
            throw new ValidationStop(ManifestRules.BoundEnvelope, null);
        }

        if (tree["envelopeVersion"] is not { Kind: NodeKind.Number, Text: "1" })
        {
            throw Stop(ManifestRules.EnvelopeVersion, "/envelopeVersion");
        }

        if (tree.StringOf("family") is not { } family || !adapters.TryGetValue(family, out var adapter))
        {
            throw Stop(ManifestRules.EnvelopeFamily, "/family");
        }

        var native = tree["native"] is { IsObject: true } n ? n : throw Stop(ManifestRules.EnvelopeFormat, "/native");
        var format = native.StringOf("format");
        var formatVersion = native.StringOf("formatVersion");
        var row = adapter.Formats.FirstOrDefault(f => f.Format == format && f.FormatVersion == formatVersion)
            ?? throw Stop(ManifestRules.EnvelopeFormat, native["format"] is null || adapter.Formats.All(f => f.Format != format) ? "/native/format" : "/native/formatVersion");

        if (native.StringOf("encoding") is not { } encoding || !row.Encodings.Contains(encoding, StringComparer.Ordinal))
        {
            throw Stop(ManifestRules.ParseSyntax, "/native/encoding");
        }

        if (native.StringOf("document") is not { } document)
        {
            throw Stop(ManifestRules.ParseNotString, DocumentPointer);
        }

        Unknown(tree, RootMembers, problems);
        Unknown(native, NativeMembers, problems);
        var capability = Text(tree, "capability", required: true, MaxCapabilityLength, problems);
        var version = Text(tree, "version", required: true, MaxVersionLength, problems);
        if (version is not null && !SemVer().IsMatch(version))
        {
            problems.Add(Problem(ManifestRules.EnvelopeSemver, "/version"));
            version = null;
        }

        var title = Text(tree, "title", required: false, MaxTitleLength, problems);
        var summary = Text(tree, "summary", required: false, MaxSummaryLength, problems);
        if (capability is not null && !IsTarget(capability, target))
        {
            problems.Add(Problem(ManifestRules.IdentityCapability, "/capability"));
        }

        return (new ManifestEnvelope(family, capability, version, title, summary, row.Format, row.FormatVersion, encoding, document), tree);
    }

    /// <summary>
    /// The identity rule (research R8, FR-011): the claim equals, ordinally, the canonical ID or the alias of the target.
    /// No trimming, no case-folding, and no other capability is ever looked up.
    /// </summary>
    public static bool IsTarget(string claim, ManifestTarget target) =>
        string.Equals(claim, target.CanonicalId, StringComparison.Ordinal) || string.Equals(claim, target.Alias, StringComparison.Ordinal);

    /// <summary>The length in Unicode scalar values (what the contract calls characters).</summary>
    public static int Length(string value) => value.EnumerateRunes().Count();

    private static string? Text(ManifestNode tree, string name, bool required, int max, List<ManifestProblem> problems)
    {
        switch (tree[name])
        {
            case null when !required:
                return null;
            case { IsString: true, Text: { } text } when Length(text) is var length && length >= 1 && length <= max:
                return text;
            case { IsString: true }:
                problems.Add(Problem(name == "version" ? ManifestRules.EnvelopeSemver : ManifestRules.EnvelopeLength, "/" + name));
                return null;
            default:
                problems.Add(Problem(ManifestRules.EnvelopeRequired, "/" + name));
                return null;
        }
    }

    private static void Unknown(ManifestNode node, string[] known, List<ManifestProblem> problems)
    {
        foreach (var (name, value) in node.Members)
        {
            if (!known.Contains(name, StringComparer.Ordinal))
            {
                problems.Add(Problem(ManifestRules.EnvelopeUnknownField, value.Pointer));
            }
        }
    }

    private static ManifestProblem Problem(ManifestRule rule, JsonPointer pointer) => new(rule, ProblemLocation.At(ManifestPart.Envelope, pointer));

    private static ManifestProblem Problem(ManifestRule rule, string pointer) => new(rule, ProblemLocation.At(ManifestPart.Envelope, pointer));

    private static ValidationStop Stop(ManifestRule rule, string pointer) => new(rule, ProblemLocation.At(ManifestPart.Envelope, pointer));

    /// <summary>The Semantic Versioning 2.0.0 pattern of the envelope schema.</summary>
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$", RegexOptions.ECMAScript)]
    private static partial Regex SemVer();

    /// <summary>The native bytes: the document string as strict UTF-8 (a lone surrogate was already refused by the parser).</summary>
    public static byte[] NativeBytes(string document) => new UTF8Encoding(false, true).GetBytes(document);
}
