using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Revisions;

/// <summary>The closed set of verification reasons, in check order (contracts/attestation-statement-v1.md).</summary>
internal static class VerificationReason
{
    public const string MalformedStatement = "malformed_statement";
    public const string UnknownStatementVersion = "unknown_statement_version";
    public const string UnknownField = "unknown_field";
    public const string UnknownAlgorithm = "unknown_algorithm";
    public const string UnknownKey = "unknown_key";
    public const string SignatureInvalid = "signature_invalid";
    public const string StatementMismatch = "statement_mismatch";
    public const string NativeDigestMismatch = "native_digest_mismatch";
    public const string ProjectionDigestMismatch = "projection_digest_mismatch";

    public static readonly IReadOnlyList<string> All =
    [
        MalformedStatement, UnknownStatementVersion, UnknownField, UnknownAlgorithm, UnknownKey,
        SignatureInvalid, StatementMismatch, NativeDigestMismatch, ProjectionDigestMismatch,
    ];
}

/// <summary>The signing key's state at verification time: active, or retired with time and reason.</summary>
internal sealed record KeyState(Guid Id, string KeyId, string Status, DateTimeOffset? RetiredAt, string? RetiredReason)
{
    public const string Active = "active";
    public const string Retired = "retired";

    public static KeyState Of(PublisherSigningKey key, SigningKeyRetirement? retirement) =>
        retirement is null
            ? new(key.Id, key.KeyId, Active, null, null)
            : new(key.Id, key.KeyId, Retired, retirement.RetiredAt, retirement.Reason);
}

/// <summary><c>verified</c>, or <c>failed</c> with one reason. Computed on every request and never stored (FR-010).</summary>
internal sealed record Verification(string Status, string? Reason, KeyState Key)
{
    public const string Verified = "verified";
    public const string Failed = "failed";

    public bool IsVerified => Status == Verified;
}

/// <summary>
/// Verifies a stored revision against its attestation (GOZ-0007 research R14, FR-010). Pure: no I/O, no clock. The
/// first failure wins, in the contract order. The key is the attestation's own key row, active or retired; the
/// verifier never re-runs an adapter (spec edge case "newer adapter version"), it recomputes the two digests over the
/// stored bytes and compares the statement with the statement those stored values give.
/// </summary>
internal static class RevisionVerifier
{
    public static Verification Verify(
        ManifestRevision revision, RevisionAttestation attestation, PublisherSigningKey key, SigningKeyRetirement? retirement, string origin)
    {
        var reason = FirstFailure(revision, attestation, key, origin);
        return new Verification(reason is null ? Verification.Verified : Verification.Failed, reason, KeyState.Of(key, retirement));
    }

    private static string? FirstFailure(ManifestRevision revision, RevisionAttestation attestation, PublisherSigningKey key, string origin)
    {
        if (AttestationStatement.ParseCanonical(attestation.Statement) is not { } statement)
        {
            return VerificationReason.MalformedStatement;
        }

        if (!IsInteger(statement["statementVersion"], AttestationStatement.CurrentVersion))
        {
            return statement.ContainsKey("statementVersion") ? VerificationReason.UnknownStatementVersion : VerificationReason.UnknownField;
        }

        if (AttestationStatement.HasUnknownOrMissingMember(statement))
        {
            return VerificationReason.UnknownField;
        }

        var stated = statement["key"]!.AsObject();
        if (!IsString(stated["algorithm"], Ed25519Signer.Algorithm) || key.Algorithm != Ed25519Signer.Algorithm)
        {
            return VerificationReason.UnknownAlgorithm;
        }

        if (!IsString(stated["id"], key.KeyId) || key.Id != attestation.SigningKeyId
            || key.PublisherId != revision.PublisherId || key.OrganizationId != revision.OrganizationId)
        {
            return VerificationReason.UnknownKey;
        }

        if (!Ed25519Signer.Verify(key.PublicKey, attestation.Statement, attestation.Signature))
        {
            return VerificationReason.SignatureInvalid;
        }

        if (!AttestationStatement.For(revision, origin, key.KeyId).ToCanonicalBytes().AsSpan().SequenceEqual(attestation.Statement))
        {
            return VerificationReason.StatementMismatch;
        }

        if (Digest(revision.NativeDocument) != revision.NativeDigest || revision.NativeDocument.Length != revision.NativeByteLength)
        {
            return VerificationReason.NativeDigestMismatch;
        }

        return Digest(revision.Projection) != revision.ProjectionDigest ? VerificationReason.ProjectionDigestMismatch : null;
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsInteger(JsonNode? node, long expected) =>
        node is JsonValue value && value.TryGetValue<long>(out var actual) && actual == expected;

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value && value.TryGetValue<string>(out var actual) && string.Equals(actual, expected, StringComparison.Ordinal);
}
