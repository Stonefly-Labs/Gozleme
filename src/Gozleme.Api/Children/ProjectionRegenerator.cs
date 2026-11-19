using System.Text;
using System.Text.Json;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Revisions;

namespace Gozleme.Api.Children;

/// <summary>The fixed refusal words of <c>projection-regenerate</c> (contracts/projection-generations.md, exit 3).</summary>
internal static class RegenerateRefusal
{
    public const string NativeDigestMismatch = "native_digest_mismatch";
    public const string AdapterUnavailable = "adapter_unavailable";
    public const string ProjectionFailed = "projection_failed";
    public const string EntriesChanged = "entries_changed";
}

/// <summary>A re-projection: canonical bytes, or the refusal word.</summary>
internal sealed record Regeneration(byte[]? Projection, string? Refusal);

/// <summary>
/// The pure core of <c>projection-regenerate</c> (research R7, maintainer-approved A4): the stored native bytes, parsed
/// with the stored encoding and projected by the adapter whose ID is the stored adapter, with an envelope rebuilt from
/// the stored summary, then canonicalized (RFC 8785). The GOZ-0006 validation rules are not re-run: the revision was
/// accepted at publication, and identity checks would fail after a capability rename. A generation never changes which
/// children a revision declares, so a projection whose (kind, nativeName) multiset differs is refused (FR-011a).
/// </summary>
internal static class ProjectionRegenerator
{
    public static Regeneration Regenerate(ManifestRevision revision, IEnumerable<IProtocolAdapter> adapters)
    {
        if (!string.Equals(RevisionVerifier.Digest(revision.NativeDocument), revision.NativeDigest, StringComparison.Ordinal))
        {
            return new(null, RegenerateRefusal.NativeDigestMismatch);
        }

        if (adapters.FirstOrDefault(a => a.Id == $"{revision.AdapterId}/{revision.AdapterVersion}") is not { } adapter)
        {
            return new(null, RegenerateRefusal.AdapterUnavailable);
        }

        byte[] projection;
        try
        {
            var envelope = Envelope(revision);
            var (tree, _) = ManifestValidator.ParseNative(envelope, revision.NativeDocument);
            projection = CanonicalJson.Canonicalize(ManifestJson.ProjectionNode(adapter.Project(tree, envelope)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // a parse or projection failure of stored bytes
        {
            return new(null, RegenerateRefusal.ProjectionFailed);
        }

        return SameKeys(ChildEntries.FromProjection(revision.Projection), ChildEntries.FromProjection(projection))
            ? new(projection, null)
            : new(null, RegenerateRefusal.EntriesChanged);
    }

    /// <summary>The envelope v1 the revision was accepted with, from its stored summary and native document.</summary>
    internal static ManifestEnvelope Envelope(ManifestRevision revision)
    {
        using var summary = JsonDocument.Parse(revision.EnvelopeSummary);
        var root = summary.RootElement;
        var native = root.GetProperty("native");
        string? Optional(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new ManifestEnvelope(
            root.GetProperty("family").GetString()!, Optional("capability"), Optional("version"), Optional("title"), Optional("summary"),
            native.GetProperty("format").GetString()!, native.GetProperty("formatVersion").GetString()!, native.GetProperty("encoding").GetString()!,
            new UTF8Encoding(false, true).GetString(revision.NativeDocument));
    }

    internal static bool SameKeys(IReadOnlyList<Manifests.Projection.ProjectionEntry> a, IReadOnlyList<Manifests.Projection.ProjectionEntry> b) =>
        a.Count == b.Count && a.Select(e => new ChildKey(e.Kind, e.NativeName)).Order(KeyOrder.Instance)
            .SequenceEqual(b.Select(e => new ChildKey(e.Kind, e.NativeName)).Order(KeyOrder.Instance));

    private sealed class KeyOrder : IComparer<ChildKey>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(ChildKey x, ChildKey y)
        {
            var c = string.CompareOrdinal(x.Kind, y.Kind);
            return c != 0 ? c : string.CompareOrdinal(x.NativeName, y.NativeName);
        }
    }
}
