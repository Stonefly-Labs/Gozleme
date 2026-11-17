using System.Collections.Frozen;

namespace Gozleme.Api.Manifests;

/// <summary>The closed problem categories, in contract order (contracts/manifest-validation.md). Adding one is a contract change.</summary>
internal enum ProblemCategory
{
    Malformed,
    TooLarge,
    UnsupportedVersion,
    UnsupportedFamily,
    UnsupportedFormat,
    UnknownField,
    ReservedClaim,
    SecretValue,
    IdentityMismatch,
    ExternalReference,
    InvalidValue,
}

/// <summary>One catalogue rule: a fixed ID, its category and one fixed English message that never contains input.</summary>
internal sealed record ManifestRule(string Id, ProblemCategory Category, string Message);

/// <summary>
/// Rule catalogue v1 (contracts/manifest-validation.md, research R9). <c>ManifestRuleCatalogueTests</c> pins it to
/// the contract table. Messages are constants: no rule ever formats a submitted value into its text (FR-012).
/// </summary>
internal static class ManifestRules
{
    public static readonly ManifestRule ParseSyntax = new("parse.syntax", ProblemCategory.Malformed, "The text does not parse in its declared encoding.");
    public static readonly ManifestRule ParseDuplicateKey = new("parse.duplicate-key", ProblemCategory.Malformed, "An object has the same member name twice.");
    public static readonly ManifestRule ParseYamlFeature = new("parse.yaml-feature", ProblemCategory.Malformed, "The YAML uses a feature that is not allowed: anchors, aliases, tags, merge keys, complex keys or more than one document.");
    public static readonly ManifestRule ParseEncoding = new("parse.encoding", ProblemCategory.Malformed, "The text contains a control character or an invalid character encoding.");
    public static readonly ManifestRule ParseNotString = new("parse.not-string", ProblemCategory.Malformed, "The native document must be a string.");
    public static readonly ManifestRule ParseFrontMatter = new("parse.front-matter", ProblemCategory.Malformed, "A SKILL.md document must start with a front matter block between two --- lines.");
    public static readonly ManifestRule ParseEmpty = new("parse.empty", ProblemCategory.Malformed, "The document is empty.");

    public static readonly ManifestRule BoundBody = new("bound.body", ProblemCategory.TooLarge, "The request body is larger than 1 MiB.");
    public static readonly ManifestRule BoundEnvelope = new("bound.envelope", ProblemCategory.TooLarge, "The envelope without the native document is larger than 16 KiB.");
    public static readonly ManifestRule BoundNative = new("bound.native", ProblemCategory.TooLarge, "The native document is larger than 512 KiB.");
    public static readonly ManifestRule BoundDepth = new("bound.depth", ProblemCategory.TooLarge, "The document is nested more than 64 levels deep.");
    public static readonly ManifestRule BoundNodes = new("bound.nodes", ProblemCategory.TooLarge, "The document has more than 50,000 values.");
    public static readonly ManifestRule BoundName = new("bound.name", ProblemCategory.TooLarge, "A member name is longer than 1,024 characters.");
    public static readonly ManifestRule BoundEntries = new("bound.entries", ProblemCategory.TooLarge, "The document declares more than 1,000 entries.");

    public static readonly ManifestRule EnvelopeVersion = new("envelope.version", ProblemCategory.UnsupportedVersion, "This envelope version is not supported; use envelopeVersion 1.");
    public static readonly ManifestRule EnvelopeFamily = new("envelope.family", ProblemCategory.UnsupportedFamily, "This capability family is not supported.");
    public static readonly ManifestRule EnvelopeFormat = new("envelope.format", ProblemCategory.UnsupportedFormat, "This native format or format version is not supported for the family.");
    public static readonly ManifestRule NativeVersionMismatch = new("native.version-mismatch", ProblemCategory.UnsupportedFormat, "The version stated in the native document is not the declared format version.");

    public static readonly ManifestRule EnvelopeUnknownField = new("envelope.unknown-field", ProblemCategory.UnknownField, "The envelope has a member that version 1 does not define.");
    public static readonly ManifestRule NativeUnknownField = new("native.unknown-field", ProblemCategory.UnknownField, "The native document has a member that its format does not define and that is not an extension.");

    public static readonly ManifestRule ReservedVocabulary = new("reserved.vocabulary", ProblemCategory.ReservedClaim, "This field asserts a reserved security claim, which a manifest cannot make.");
    public static readonly ManifestRule ReservedA2aSignatures = new("reserved.a2a-signatures", ProblemCategory.ReservedClaim, "Agent Card signatures are reserved; Gözleme signs revisions itself.");
    public static readonly ManifestRule ReservedMcpRegistryMeta = new("reserved.mcp-registry-meta", ProblemCategory.ReservedClaim, "Registry-asserted MCP metadata is reserved; only publisher-provided metadata may be declared.");
    public static readonly ManifestRule ReservedMcpNamespace = new("reserved.mcp-namespace", ProblemCategory.ReservedClaim, "The io.modelcontextprotocol/ metadata namespace is reserved by MCP.");

    public static readonly ManifestRule SecretField = new("secret.field", ProblemCategory.SecretValue, "This field carries a credential; declare it without a value or use a secret reference.");
    public static readonly ManifestRule SecretPattern = new("secret.pattern", ProblemCategory.SecretValue, "This value looks like a credential; remove it or use a secret reference.");
    public static readonly ManifestRule SecretName = new("secret.name", ProblemCategory.SecretValue, "A member name in this object looks like a credential.");

    public static readonly ManifestRule IdentityCapability = new("identity.capability", ProblemCategory.IdentityMismatch, "The capability claim is not this capability's canonical ID or alias.");

    public static readonly ManifestRule RefExternal = new("ref.external", ProblemCategory.ExternalReference, "References outside the document are not allowed; nothing is fetched.");
    public static readonly ManifestRule RefDialect = new("ref.dialect", ProblemCategory.ExternalReference, "This schema dialect is not bundled; nothing is fetched.");

    public static readonly ManifestRule EnvelopeRequired = new("envelope.required", ProblemCategory.InvalidValue, "A required envelope member is missing or has the wrong type.");
    public static readonly ManifestRule EnvelopeSemver = new("envelope.semver", ProblemCategory.InvalidValue, "The version must be a Semantic Versioning 2.0.0 version of at most 256 characters.");
    public static readonly ManifestRule EnvelopeLength = new("envelope.length", ProblemCategory.InvalidValue, "This envelope value is empty or longer than allowed.");
    public static readonly ManifestRule NativeSchema = new("native.schema", ProblemCategory.InvalidValue, "This value does not match the format's schema.");
    public static readonly ManifestRule NativeConsistency = new("native.consistency", ProblemCategory.InvalidValue, "The declared entries are inconsistent with the declared capabilities or repeat a name.");
    public static readonly ManifestRule NativeMetaKey = new("native.meta-key", ProblemCategory.InvalidValue, "A _meta key must start with a reverse-DNS prefix and a slash.");
    public static readonly ManifestRule RefUnresolved = new("ref.unresolved", ProblemCategory.InvalidValue, "This reference does not resolve within the document.");

    /// <summary>Every rule, in contract table order.</summary>
    public static readonly IReadOnlyList<ManifestRule> All =
    [
        ParseSyntax, ParseDuplicateKey, ParseYamlFeature, ParseEncoding, ParseNotString, ParseFrontMatter, ParseEmpty,
        BoundBody, BoundEnvelope, BoundNative, BoundDepth, BoundNodes, BoundName, BoundEntries,
        EnvelopeVersion, EnvelopeFamily, EnvelopeFormat, NativeVersionMismatch,
        EnvelopeUnknownField, NativeUnknownField,
        ReservedVocabulary, ReservedA2aSignatures, ReservedMcpRegistryMeta, ReservedMcpNamespace,
        SecretField, SecretPattern, SecretName,
        IdentityCapability,
        RefExternal, RefDialect,
        EnvelopeRequired, EnvelopeSemver, EnvelopeLength, NativeSchema, NativeConsistency, NativeMetaKey, RefUnresolved,
    ];

    public static readonly FrozenDictionary<string, ManifestRule> ById = All.ToFrozenDictionary(r => r.Id, StringComparer.Ordinal);

    /// <summary>The wire name of a category (snake case, closed set; also a metric label value).</summary>
    public static string Name(ProblemCategory category) => category switch
    {
        ProblemCategory.Malformed => "malformed",
        ProblemCategory.TooLarge => "too_large",
        ProblemCategory.UnsupportedVersion => "unsupported_version",
        ProblemCategory.UnsupportedFamily => "unsupported_family",
        ProblemCategory.UnsupportedFormat => "unsupported_format",
        ProblemCategory.UnknownField => "unknown_field",
        ProblemCategory.ReservedClaim => "reserved_claim",
        ProblemCategory.SecretValue => "secret_value",
        ProblemCategory.IdentityMismatch => "identity_mismatch",
        ProblemCategory.ExternalReference => "external_reference",
        _ => "invalid_value",
    };
}
