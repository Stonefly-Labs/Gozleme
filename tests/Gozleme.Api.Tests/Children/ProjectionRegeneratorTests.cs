using System.Text;
using Gozleme.Api.Children;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Projection;
using Gozleme.Api.Revisions;
using Gozleme.Api.Tests.Manifests;

namespace Gozleme.Api.Tests.Children;

/// <summary>
/// GOZ-0008 T040 (research R7, maintainer-approved A4): the stored native bytes of every family re-project to
/// byte-identical canonical bytes (RFC 8785) with the stored adapter and an envelope rebuilt from the stored summary;
/// validation rules are never called; tampered bytes, an unregistered adapter and a changed entry multiset are refused.
/// </summary>
public sealed class ProjectionRegeneratorTests
{
    /// <summary>A revision stored exactly as publication stores it, from a valid sample.</summary>
    private static ManifestRevision Stored(string sampleId)
    {
        var accepted = ManifestTestKit.Accepted(ManifestTestKit.Validate(ManifestSamples.Get(sampleId).Body(ManifestTestKit.CanonicalId)));
        var native = EnvelopeReader.NativeBytes(accepted.Native.Document);
        var projection = CanonicalJson.Canonicalize(ManifestJson.ProjectionNode(accepted.Projection));
        var (adapterId, adapterVersion) = AttestationStatement.SplitAdapter(accepted.Projection.Adapter);
        return new ManifestRevision
        {
            Family = accepted.Envelope.Family,
            EnvelopeSummary = ManifestJson.SummaryJson(accepted.Envelope),
            NativeEncoding = accepted.Native.Encoding,
            NativeDocument = native,
            NativeDigest = RevisionVerifier.Digest(native),
            Projection = projection,
            ProjectionVersion = accepted.Projection.ProjectionVersion,
            ProjectionDigest = RevisionVerifier.Digest(projection),
            AdapterId = adapterId,
            AdapterVersion = adapterVersion,
        };
    }

    public static TheoryData<string> Valid => [.. ManifestSamples.Valid.Select(s => s.Id)];

    [Theory]
    [MemberData(nameof(Valid))]
    public void Every_family_re_projects_to_byte_identical_canonical_bytes(string sampleId)
    {
        var revision = Stored(sampleId);
        var regeneration = ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All);
        Assert.Null(regeneration.Refusal);
        Assert.Equal(revision.Projection, regeneration.Projection);
        Assert.True(CanonicalJson.IsCanonical(regeneration.Projection));
    }

    [Fact]
    public void Validation_rules_are_not_called()
    {
        var spy = new SpyAdapter(ManifestAdapters.All.Single(a => a.Family == "mcp"));
        var revision = Stored("valid-stateless-2026-07-28");
        Assert.Null(ProjectionRegenerator.Regenerate(revision, [spy]).Refusal);
        Assert.Equal(1, spy.Projections);
        Assert.Equal(0, spy.Validations);
    }

    [Fact]
    public void Tampered_native_bytes_are_refused()
    {
        var revision = Stored("valid-stateless-2026-07-28");
        revision.NativeDocument = [.. revision.NativeDocument, (byte)' '];
        Assert.Equal(RegenerateRefusal.NativeDigestMismatch, ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All).Refusal);
    }

    [Fact]
    public void An_unregistered_adapter_is_refused()
    {
        var revision = Stored("valid-stateless-2026-07-28");
        revision.AdapterVersion = "2";
        Assert.Equal(RegenerateRefusal.AdapterUnavailable, ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All).Refusal);
    }

    [Fact]
    public void Native_bytes_that_no_longer_parse_are_refused()
    {
        var revision = Stored("valid-stateless-2026-07-28");
        revision.NativeDocument = Encoding.UTF8.GetBytes("{not json");
        revision.NativeDigest = RevisionVerifier.Digest(revision.NativeDocument);
        Assert.Equal(RegenerateRefusal.ProjectionFailed, ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All).Refusal);
    }

    [Fact]
    public void A_changed_entry_multiset_is_detected()
    {
        var revision = Stored("valid-stateless-2026-07-28");
        var projection = Encoding.UTF8.GetString(revision.Projection).Replace("\"get_forecast\"", "\"get_forecast_v2\"", StringComparison.Ordinal);
        revision.Projection = Encoding.UTF8.GetBytes(projection);
        Assert.Equal(RegenerateRefusal.EntriesChanged, ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All).Refusal);

        ProjectionEntry[] keys = [new("tool", "a", "A"), new("tool", "a", null), new("prompt", "p", null)];
        Assert.True(ProjectionRegenerator.SameKeys(keys, [new("prompt", "p", "P"), new("tool", "a", null), new("tool", "a", "other title")]));
        Assert.False(ProjectionRegenerator.SameKeys(keys, [new("tool", "a", null), new("prompt", "p", null), new("prompt", "p", null)]));
        Assert.False(ProjectionRegenerator.SameKeys(keys, [new("tool", "a", null), new("prompt", "p", null)]));
    }

    /// <summary>Counts calls; fails the test if any validation hook runs.</summary>
    private sealed class SpyAdapter(IProtocolAdapter inner) : IProtocolAdapter
    {
        public int Projections { get; private set; }

        public int Validations { get; private set; }

        public string Id => inner.Id;

        public string Family => inner.Family;

        public IReadOnlyList<NativeFormat> Formats => inner.Formats;

        public int CountEntries(ManifestNode tree) => inner.CountEntries(tree);

        public void Validate(ManifestNode tree, NativeContext context) => Validations++;

        public IEnumerable<NamePosition> ReservedNamePositions(ManifestNode tree)
        {
            Validations++;
            return inner.ReservedNamePositions(tree);
        }

        public IEnumerable<ManifestNode> SecretFields(ManifestNode tree)
        {
            Validations++;
            return inner.SecretFields(tree);
        }

        public IEnumerable<ManifestNode> ReferenceScopes(ManifestNode tree)
        {
            Validations++;
            return inner.ReferenceScopes(tree);
        }

        public ManifestProjection Project(ManifestNode tree, ManifestEnvelope envelope)
        {
            Projections++;
            return inner.Project(tree, envelope);
        }
    }
}
