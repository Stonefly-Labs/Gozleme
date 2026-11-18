using System.Text;
using Gozleme.Api.Revisions;
using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>A signed revision built in memory, for the pure statement and verifier suites.</summary>
internal sealed class RevisionTestKit
{
    public const string Origin = "registry.demo.gozleme.test";

    public RevisionTestKit()
    {
        var (seed, publicKey) = Ed25519Signer.Create();
        Seed = seed;
        var publisherId = Guid.CreateVersion7();
        var organizationId = Guid.CreateVersion7();
        Key = new PublisherSigningKey
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            PublisherId = publisherId,
            Generation = 1,
            KeyId = Ed25519Signer.KeyId(publicKey),
            Algorithm = Ed25519Signer.Algorithm,
            PublicKey = publicKey,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var native = Encoding.UTF8.GetBytes("""{"name":"io.example/weather","version":"1.0.0"}""");
        var projection = CanonicalJson.Canonicalize("""{"projectionVersion":"1","adapter":"mcp/1","title":null}"""u8);
        Revision = new ManifestRevision
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            PublisherId = publisherId,
            CapabilityId = Guid.CreateVersion7(),
            Sequence = 1,
            Version = "1.0.0",
            VersionKey = "1.0.0",
            Family = "mcp",
            EnvelopeSummary = "{}",
            NativeFormat = "mcp-server-manifest",
            NativeFormatVersion = "1",
            NativeEncoding = "json",
            NativeDocument = native,
            NativeDigest = RevisionVerifier.Digest(native),
            NativeByteLength = native.Length,
            Projection = projection,
            ProjectionVersion = "1",
            ProjectionDigest = RevisionVerifier.Digest(projection),
            AdapterId = "mcp",
            AdapterVersion = "1",
            RuleCatalogueVersion = "1",
            SchemaBundleVersion = "1",
            PublishedBy = Guid.CreateVersion7(),
            PublishedAt = new DateTimeOffset(2026, 11, 18, 10, 15, 30, TimeSpan.Zero).AddTicks(1_234_560),
        };
        Attestation = Sign(AttestationStatement.For(Revision, Origin, Key.KeyId).ToCanonicalBytes());
    }

    public byte[] Seed { get; }

    public PublisherSigningKey Key { get; }

    public ManifestRevision Revision { get; }

    public RevisionAttestation Attestation { get; }

    /// <summary>An attestation over <paramref name="statement"/>, validly signed with this kit's key.</summary>
    public RevisionAttestation Sign(byte[] statement) => new()
    {
        RevisionId = Revision.Id,
        OrganizationId = Revision.OrganizationId,
        SigningKeyId = Key.Id,
        StatementVersion = 1,
        Statement = statement,
        Signature = Ed25519Signer.Sign(Seed, statement),
    };

    public Verification Verify(RevisionAttestation? attestation = null, PublisherSigningKey? key = null, SigningKeyRetirement? retirement = null, ManifestRevision? revision = null) =>
        RevisionVerifier.Verify(revision ?? Revision, attestation ?? Attestation, key ?? Key, retirement, Origin);
}
