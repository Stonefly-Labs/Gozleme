using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.Revisions;
using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>GOZ-0007 T033 (contracts/attestation-statement-v1.md, research R4; FR-008, FR-012).</summary>
public sealed class AttestationStatementTests
{
    private readonly RevisionTestKit _kit = new();

    private JsonObject Statement => JsonNode.Parse(_kit.Attestation.Statement)!.AsObject();

    [Fact]
    public void The_statement_has_exactly_the_contract_members()
    {
        var statement = Statement;
        Assert.Equal(["adapter", "capability", "key", "native", "projection", "publishedAt", "publisher", "registryOrigin", "sequence", "statementVersion", "type", "version"],
            statement.Select(m => m.Key));
        Assert.Equal(["byteLength", "digest", "encoding", "format", "formatVersion"], statement["native"]!.AsObject().Select(m => m.Key));
        Assert.Equal(["id", "version"], statement["adapter"]!.AsObject().Select(m => m.Key));
        Assert.Equal(["digest", "version"], statement["projection"]!.AsObject().Select(m => m.Key));
        Assert.Equal(["algorithm", "id"], statement["key"]!.AsObject().Select(m => m.Key));
        Assert.False(AttestationStatement.HasUnknownOrMissingMember(statement));
    }

    [Fact]
    public void Field_values_come_from_the_revision_and_name_canonical_ids_only()
    {
        var s = Statement;
        var r = _kit.Revision;
        Assert.Equal(1, s["statementVersion"]!.GetValue<int>());
        Assert.Equal("gozleme.revision-attestation", s["type"]!.GetValue<string>());
        Assert.Equal(RevisionTestKit.Origin, s["registryOrigin"]!.GetValue<string>());
        Assert.Equal($"gz://{RevisionTestKit.Origin}/pub/{r.PublisherId:D}", s["publisher"]!.GetValue<string>());
        Assert.Equal($"gz://{RevisionTestKit.Origin}/cap/{r.CapabilityId:D}", s["capability"]!.GetValue<string>());
        Assert.Equal(("1.0.0", 1), (s["version"]!.GetValue<string>(), s["sequence"]!.GetValue<int>()));
        Assert.Equal(r.NativeDigest, s["native"]!["digest"]!.GetValue<string>());
        Assert.Equal(r.NativeByteLength, s["native"]!["byteLength"]!.GetValue<int>());
        Assert.Equal(r.ProjectionDigest, s["projection"]!["digest"]!.GetValue<string>());
        Assert.Equal((_kit.Key.KeyId, "Ed25519"), (s["key"]!["id"]!.GetValue<string>(), s["key"]!["algorithm"]!.GetValue<string>()));
        var text = Encoding.UTF8.GetString(_kit.Attestation.Statement);
        foreach (var banned in new[] { "alias", "handle", "displayName", "organization", "delegat", "transfer", "chain", "witness", "transparency" })
        {
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PublishedAt_is_RFC3339_UTC_with_six_fractional_digits()
    {
        Assert.Equal("2026-11-18T10:15:30.123456Z", Statement["publishedAt"]!.GetValue<string>());
        Assert.Equal("2026-11-18T10:15:30.000000Z", AttestationStatement.Timestamp(new DateTimeOffset(2026, 11, 18, 12, 15, 30, TimeSpan.FromHours(2))));
    }

    [Theory]
    [InlineData("mcp/1", "mcp", "1")]
    [InlineData("model/1", "model", "1")]
    [InlineData("mcp/2", "mcp", "2")]
    public void The_adapter_id_is_split_at_the_slash(string adapter, string id, string version) =>
        Assert.Equal((id, version), AttestationStatement.SplitAdapter(adapter));

    [Theory]
    [InlineData("mcp")]
    [InlineData("/1")]
    [InlineData("mcp/")]
    [InlineData("a/b/c")]
    public void A_malformed_adapter_id_is_refused(string adapter) =>
        Assert.Throws<ArgumentException>(() => AttestationStatement.SplitAdapter(adapter));

    [Fact]
    public void Identical_input_gives_byte_identical_canonical_bytes_and_signatures()
    {
        var again = AttestationStatement.For(_kit.Revision, RevisionTestKit.Origin, _kit.Key.KeyId).ToCanonicalBytes();
        Assert.Equal(_kit.Attestation.Statement, again);
        Assert.True(CanonicalJson.IsCanonical(again));
        Assert.Equal(_kit.Attestation.Signature, Ed25519Signer.Sign(_kit.Seed, again)); // Ed25519 is deterministic
    }
}
