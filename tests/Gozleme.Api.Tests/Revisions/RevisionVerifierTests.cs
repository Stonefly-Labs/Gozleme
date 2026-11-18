using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.Revisions;
using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>
/// GOZ-0007 T050 and T059 (research R14, contracts/attestation-statement-v1.md "Gözleme verification result"): one
/// case per reason in contract order, the key status of a retired key, and US4-AS2 — statements submitted to the pure
/// verifier with an added delegation member, an unknown version, an unknown algorithm or another publisher's key are
/// refused (A6: there is no public verify endpoint).
/// </summary>
public sealed class RevisionVerifierTests
{
    private readonly RevisionTestKit _kit = new();

    /// <summary>A validly signed statement edited by <paramref name="edit"/>, so only the edit can fail it.</summary>
    private RevisionAttestation Resigned(Action<JsonObject> edit)
    {
        var statement = JsonNode.Parse(_kit.Attestation.Statement)!.AsObject();
        edit(statement);
        return _kit.Sign(CanonicalJson.Canonicalize(statement));
    }

    private static void Failed(string reason, Verification verification)
    {
        Assert.Equal(Verification.Failed, verification.Status);
        Assert.Equal(reason, verification.Reason);
        Assert.False(verification.IsVerified);
    }

    [Fact]
    public void A_stored_revision_verifies_with_an_active_key()
    {
        var verification = _kit.Verify();
        Assert.Equal((Verification.Verified, (string?)null), (verification.Status, verification.Reason));
        Assert.Equal((KeyState.Active, _kit.Key.Id, _kit.Key.KeyId), (verification.Key.Status, verification.Key.Id, verification.Key.KeyId));
        Assert.Null(verification.Key.RetiredAt);
    }

    [Fact]
    public void A_retired_key_still_verifies_and_reports_its_retirement()
    {
        var retired = new SigningKeyRetirement { KeyId = _kit.Key.Id, SuccessorKeyId = Guid.CreateVersion7(), Reason = "compromised", RetiredAt = DateTimeOffset.UnixEpoch };
        var verification = _kit.Verify(retirement: retired);
        Assert.True(verification.IsVerified);
        Assert.Equal((KeyState.Retired, (DateTimeOffset?)DateTimeOffset.UnixEpoch, "compromised"), (verification.Key.Status, verification.Key.RetiredAt, verification.Key.RetiredReason));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"b\":1, \"a\":2}")]
    [InlineData("[1]")]
    public void Bytes_that_are_not_their_own_canonical_object_are_malformed(string text) =>
        Failed(VerificationReason.MalformedStatement, _kit.Verify(_kit.Sign(Encoding.UTF8.GetBytes(text))));

    [Fact]
    public void A_reordered_but_validly_signed_statement_is_malformed()
    {
        var reordered = Encoding.UTF8.GetString(_kit.Attestation.Statement).Replace("{\"adapter\"", "{ \"adapter\"", StringComparison.Ordinal);
        Failed(VerificationReason.MalformedStatement, _kit.Verify(_kit.Sign(Encoding.UTF8.GetBytes(reordered))));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void An_unknown_statement_version_is_refused(int version) =>
        Failed(VerificationReason.UnknownStatementVersion, _kit.Verify(Resigned(s => s["statementVersion"] = version)));

    [Fact]
    public void A_non_integer_statement_version_is_unknown_and_a_missing_one_is_an_unknown_field()
    {
        Failed(VerificationReason.UnknownStatementVersion, _kit.Verify(Resigned(s => s["statementVersion"] = "1")));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s.Remove("statementVersion"))));
    }

    [Fact]
    public void A_delegation_member_at_the_top_level_or_nested_is_an_unknown_field()
    {
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["delegation"] = new JsonObject { ["to"] = "gz://elsewhere/pub/x" })));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["key"]!["delegatedBy"] = "x")));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["chain"] = new JsonArray())));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["native"]!.AsObject().Remove("digest"))));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["adapter"] = "mcp/1")));
        Failed(VerificationReason.UnknownField, _kit.Verify(Resigned(s => s["version"] = new JsonObject())));
    }

    [Theory]
    [InlineData("ES256")]
    [InlineData("ed25519")]
    public void An_unknown_algorithm_is_refused(string algorithm) =>
        Failed(VerificationReason.UnknownAlgorithm, _kit.Verify(Resigned(s => s["key"]!["algorithm"] = algorithm)));

    [Fact]
    public void Another_publishers_key_is_an_unknown_key()
    {
        var other = new RevisionTestKit();
        Failed(VerificationReason.UnknownKey, _kit.Verify(Resigned(s => s["key"]!["id"] = other.Key.KeyId)));

        // The attestation's own key row belongs to another publisher, even with a matching statement.
        var foreignKey = new PublisherSigningKey
        {
            Id = _kit.Key.Id,
            OrganizationId = _kit.Key.OrganizationId,
            PublisherId = Guid.CreateVersion7(),
            Generation = 1,
            KeyId = _kit.Key.KeyId,
            Algorithm = "Ed25519",
            PublicKey = _kit.Key.PublicKey,
        };
        Failed(VerificationReason.UnknownKey, _kit.Verify(key: foreignKey));
        var otherRow = new PublisherSigningKey
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = _kit.Key.OrganizationId,
            PublisherId = _kit.Key.PublisherId,
            Generation = 2,
            KeyId = _kit.Key.KeyId,
            Algorithm = "Ed25519",
            PublicKey = _kit.Key.PublicKey,
        };
        Failed(VerificationReason.UnknownKey, _kit.Verify(key: otherRow));
    }

    [Fact]
    public void A_changed_statement_byte_or_signature_is_signature_invalid()
    {
        var statement = (byte[])_kit.Attestation.Statement.Clone();
        var at = Encoding.UTF8.GetString(statement).IndexOf("\"1.0.0\"", StringComparison.Ordinal) + 5;
        statement[at] = (byte)'1'; // "1.0.1": still canonical
        Failed(VerificationReason.SignatureInvalid, _kit.Verify(new RevisionAttestation
        {
            RevisionId = _kit.Revision.Id,
            SigningKeyId = _kit.Key.Id,
            Statement = statement,
            Signature = _kit.Attestation.Signature,
        }));
        var signature = (byte[])_kit.Attestation.Signature.Clone();
        signature[0] ^= 1;
        Failed(VerificationReason.SignatureInvalid, _kit.Verify(new RevisionAttestation
        {
            RevisionId = _kit.Revision.Id,
            SigningKeyId = _kit.Key.Id,
            Statement = _kit.Attestation.Statement,
            Signature = signature,
        }));
    }

    [Fact]
    public void A_validly_signed_statement_that_differs_from_the_row_is_a_mismatch()
    {
        Failed(VerificationReason.StatementMismatch, _kit.Verify(Resigned(s => s["sequence"] = 2)));
        Failed(VerificationReason.StatementMismatch, _kit.Verify(Resigned(s => s["registryOrigin"] = "registry.other.test")));
        Failed(VerificationReason.StatementMismatch, _kit.Verify(Resigned(s => s["native"]!["digest"] = "sha256:" + new string('0', 64))));
    }

    [Fact]
    public void Altered_stored_bytes_fail_their_digest()
    {
        var revision = Copy(_kit.Revision);
        revision.NativeDocument = [.. revision.NativeDocument];
        revision.NativeDocument[3] ^= 1;
        Failed(VerificationReason.NativeDigestMismatch, _kit.Verify(revision: revision));

        revision = Copy(_kit.Revision);
        revision.Projection = [.. revision.Projection];
        revision.Projection[3] ^= 1;
        Failed(VerificationReason.ProjectionDigestMismatch, _kit.Verify(revision: revision));
    }

    [Fact]
    public void The_reasons_are_the_closed_contract_set_in_order()
    {
        Assert.Equal(["malformed_statement", "unknown_statement_version", "unknown_field", "unknown_algorithm", "unknown_key", "signature_invalid",
            "statement_mismatch", "native_digest_mismatch", "projection_digest_mismatch"], VerificationReason.All);
        Assert.Equal(Ed25519Signer.Algorithm, _kit.Key.Algorithm);
    }

    private static ManifestRevision Copy(ManifestRevision r) => (ManifestRevision)typeof(ManifestRevision).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(r, null)!;
}
