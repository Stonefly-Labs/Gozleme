using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gozleme.Api.Publishers;

namespace Gozleme.Api.Revisions;

/// <summary>Error bodies of the revision API (contracts/revision-api.openapi.yaml <c>Error</c>; A4).</summary>
internal sealed record RevisionError(string Error)
{
    public const string Forbidden = "forbidden";
    public const string Suspended = "suspended";
    public const string VersionExists = "version_exists";
    public const string RateLimited = "rate_limited";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string TooLarge = "too_large";
    public const string Unavailable = "unavailable";
}

/// <summary><c>KeyStatus</c>: the signing key named by an attestation, active or retired.</summary>
internal sealed record KeyStatusView(
    Guid Id,
    string KeyId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetiredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RetiredReason)
{
    public static KeyStatusView From(KeyState key) => new(key.Id, key.KeyId, key.Status, key.RetiredAt, key.RetiredReason);
}

/// <summary><c>Verification</c>: computed now from the stored bytes; <c>reason</c> is <c>null</c> when verified.</summary>
internal sealed record VerificationView(string Status, string? Reason, KeyStatusView Key)
{
    public static VerificationView From(Verification verification) => new(verification.Status, verification.Reason, KeyStatusView.From(verification.Key));
}

internal sealed record RevisionSummary(
    Guid Id, string CapabilityId, string Version, int Sequence, DateTimeOffset PublishedAt, Guid PublishedBy, string NativeDigest, VerificationView Verification);

/// <summary>Revisions in publication order; <c>next</c> is the <c>after</c> value of the next page, or null at the end. No "latest".</summary>
internal sealed record RevisionList(bool PublisherSuspended, IReadOnlyList<RevisionSummary> Revisions, int? Next);

internal sealed record NativeView(string Format, string FormatVersion, string Encoding, string Document, string Digest, int ByteLength);

internal sealed record AdapterView(string Id, string Version);

internal sealed record AttestationView(int StatementVersion, JsonObject Statement, string Algorithm, string KeyId, string Proof);

/// <summary><c>RevisionDetail</c>: the summary members, then the stored revision and its attestation.</summary>
internal sealed record RevisionDetail(
    Guid Id,
    string CapabilityId,
    string Version,
    int Sequence,
    DateTimeOffset PublishedAt,
    Guid PublishedBy,
    string NativeDigest,
    VerificationView Verification,
    string PublisherId,
    bool PublisherSuspended,
    JsonNode Envelope,
    NativeView Native,
    JsonNode Projection,
    string ProjectionDigest,
    AdapterView Adapter,
    string RuleCatalogueVersion,
    string SchemaBundleVersion,
    AttestationView Attestation);

internal sealed record SigningKeyView(
    Guid Id,
    string KeyId,
    string Algorithm,
    int Generation,
    DateTimeOffset CreatedAt,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetiredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RetiredReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SuccessorId);

/// <summary>Keys by generation; empty when the publisher has no key yet. No private or encrypted material.</summary>
internal sealed record SigningKeyList(Guid PublisherId, IReadOnlyList<SigningKeyView> Keys);

/// <summary>The fixed wording of what an attestation proves and does not prove (research R17, FR-013).</summary>
internal static class AttestationProof
{
    public static string For(string origin, Guid publisherId) =>
        $"Signed by registry {origin} for publisher {CanonicalId.Publisher(origin, publisherId)} with a registry-held key. "
        + "This shows that this registry accepted the revision from an authorized publisher member. It is not a signature made by the publisher.";
}
