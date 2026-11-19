using System.Text.Json;
using System.Text.Json.Nodes;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Children;

/// <summary>
/// Projection entries as child assignment reads them, and the publish-time rules: duplicates (maintainer Q2, research
/// R3) and U+0000 (maintainer ruling F3), contracts/publish-delta.md.
/// </summary>
internal static class ChildEntries
{
    /// <summary>
    /// One <c>publish.duplicate_entry</c> problem for every entry of a key that occurs more than once, the first one
    /// included, ordered by index, at <c>{part: "projection", pointer: "/entries/&lt;i&gt;"}</c>. No name is echoed.
    /// </summary>
    public static IReadOnlyList<ManifestProblem> Duplicates(IReadOnlyList<ProjectionEntry> entries)
    {
        var counts = new Dictionary<ChildKey, int>();
        foreach (var entry in entries)
        {
            var key = new ChildKey(entry.Kind, entry.NativeName);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return [.. entries.Select((entry, i) => (entry, i))
            .Where(x => counts[new ChildKey(x.entry.Kind, x.entry.NativeName)] > 1)
            .Select(x => new ManifestProblem(PublishRules.DuplicateEntry, ProblemLocation.At(ManifestPart.Projection, $"/entries/{x.i}")))];
    }

    /// <summary>
    /// One <c>publish.nul_character</c> problem for every string of the projection tree <paramref name="projection"/>
    /// that holds U+0000, in document order, at <c>{part: "projection", pointer: &lt;that string&gt;}</c>. No value is echoed.
    /// </summary>
    public static IReadOnlyList<ManifestProblem> NulCharacters(JsonNode? projection)
    {
        var problems = new List<ManifestProblem>();
        Walk(projection, "");
        return problems;

        void Walk(JsonNode? node, string pointer)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        Walk(value, $"{pointer}/{name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}");
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        Walk(array[i], $"{pointer}/{i}");
                    }

                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Contains('\0', StringComparison.Ordinal):
                    problems.Add(new ManifestProblem(PublishRules.NulCharacter, ProblemLocation.At(ManifestPart.Projection, pointer)));
                    break;
            }
        }
    }

    /// <summary>Whether stored projection bytes hold U+0000 in any string (the install step skips such a revision).</summary>
    public static bool HasNulCharacter(ReadOnlySpan<byte> projection) => NulCharacters(JsonNode.Parse(projection)).Count > 0;

    /// <summary>The GOZ-0006 rejected body for <paramref name="problems"/>: at most 50, with <c>truncated</c>.</summary>
    public static RejectedResult Rejected(IReadOnlyList<ManifestProblem> problems) =>
        new([.. problems.Take(ManifestValidator.MaxProblems).Select(ValidationProblem.From)], problems.Count > ManifestValidator.MaxProblems);

    /// <summary>The entries of stored projection v1 bytes (the install step and re-projection).</summary>
    public static IReadOnlyList<ProjectionEntry> FromProjection(ReadOnlySpan<byte> projection)
    {
        using var document = JsonDocument.Parse(projection.ToArray());
        if (!document.RootElement.TryGetProperty("entries"u8, out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. entries.EnumerateArray().Select(e => new ProjectionEntry(
            e.GetProperty("kind"u8).GetString()!,
            e.GetProperty("nativeName"u8).GetString()!,
            e.TryGetProperty("title"u8, out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null))];
    }
}
