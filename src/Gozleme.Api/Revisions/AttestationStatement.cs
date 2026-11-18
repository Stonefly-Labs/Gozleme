using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Revisions;

/// <summary>
/// Attestation statement v1 (GOZ-0007 contracts/attestation-statement-v1.md, research R4; FR-008, FR-012): exactly the
/// listed members, canonical IDs only, and no alias, handle, display name, organization or account member. There is no
/// member for delegation, transfer, chains, witnesses or transparency in v1. The signed bytes are the RFC 8785
/// canonical form (<see cref="CanonicalJson"/>).
/// </summary>
internal sealed record AttestationStatement(
    string RegistryOrigin,
    Guid PublisherId,
    Guid CapabilityId,
    string Version,
    int Sequence,
    string NativeFormat,
    string NativeFormatVersion,
    string NativeEncoding,
    string NativeDigest,
    int NativeByteLength,
    string AdapterId,
    string AdapterVersion,
    string ProjectionVersion,
    string ProjectionDigest,
    DateTimeOffset PublishedAt,
    string KeyId)
{
    public const int CurrentVersion = 1;
    public const string Type = "gozleme.revision-attestation";

    /// <summary>The closed member set: top level, then each nested object's members.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>?> Members = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal)
    {
        ["statementVersion"] = null,
        ["type"] = null,
        ["registryOrigin"] = null,
        ["publisher"] = null,
        ["capability"] = null,
        ["version"] = null,
        ["sequence"] = null,
        ["native"] = Set("format", "formatVersion", "encoding", "digest", "byteLength"),
        ["adapter"] = Set("id", "version"),
        ["projection"] = Set("version", "digest"),
        ["publishedAt"] = null,
        ["key"] = Set("id", "algorithm"),
    };

    /// <summary>The statement for a stored revision signed with the key named <paramref name="keyId"/>.</summary>
    public static AttestationStatement For(ManifestRevision revision, string origin, string keyId) => new(
        origin, revision.PublisherId, revision.CapabilityId, revision.Version, revision.Sequence,
        revision.NativeFormat, revision.NativeFormatVersion, revision.NativeEncoding, revision.NativeDigest, revision.NativeByteLength,
        revision.AdapterId, revision.AdapterVersion, revision.ProjectionVersion, revision.ProjectionDigest, revision.PublishedAt, keyId);

    /// <summary>GOZ-0006 adapter IDs are <c>&lt;id&gt;/&lt;version&gt;</c>; v1 records the two parts (A1: <c>mcp/1</c> → <c>mcp</c>, <c>1</c>).</summary>
    public static (string Id, string Version) SplitAdapter(string adapter)
    {
        var slash = adapter.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == adapter.Length - 1 || adapter.IndexOf('/', slash + 1) >= 0)
        {
            throw new ArgumentException("An adapter ID is <id>/<version>.", nameof(adapter));
        }

        return (adapter[..slash], adapter[(slash + 1)..]);
    }

    /// <summary>RFC 3339 UTC with exactly six fractional digits, PostgreSQL's <c>timestamptz</c> precision.</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public JsonObject ToJson() => new()
    {
        ["statementVersion"] = CurrentVersion,
        ["type"] = Type,
        ["registryOrigin"] = RegistryOrigin,
        ["publisher"] = CanonicalId.Publisher(RegistryOrigin, PublisherId),
        ["capability"] = CanonicalId.Capability(RegistryOrigin, CapabilityId),
        ["version"] = Version,
        ["sequence"] = Sequence,
        ["native"] = new JsonObject
        {
            ["format"] = NativeFormat,
            ["formatVersion"] = NativeFormatVersion,
            ["encoding"] = NativeEncoding,
            ["digest"] = NativeDigest,
            ["byteLength"] = NativeByteLength,
        },
        ["adapter"] = new JsonObject { ["id"] = AdapterId, ["version"] = AdapterVersion },
        ["projection"] = new JsonObject { ["version"] = ProjectionVersion, ["digest"] = ProjectionDigest },
        ["publishedAt"] = Timestamp(PublishedAt),
        ["key"] = new JsonObject { ["id"] = KeyId, ["algorithm"] = Ed25519Signer.Algorithm },
    };

    /// <summary>The signed bytes.</summary>
    public byte[] ToCanonicalBytes() => CanonicalJson.Canonicalize(ToJson());

    /// <summary>The statement as a JSON object, or null when the bytes are not their own canonical JSON object form.</summary>
    public static JsonObject? ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (!CanonicalJson.IsCanonical(bytes))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a member outside the closed set exists at any level, or a listed member is missing.</summary>
    public static bool HasUnknownOrMissingMember(JsonObject statement)
    {
        if (statement.Count != Members.Count)
        {
            return true;
        }

        foreach (var (name, nested) in Members)
        {
            if (!statement.TryGetPropertyValue(name, out var value))
            {
                return true;
            }

            if (nested is not null && (value is not JsonObject inner || inner.Count != nested.Count || inner.Any(m => !nested.Contains(m.Key))))
            {
                return true;
            }

            if (nested is null && value is JsonObject or JsonArray)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);
}
