using System.Text.Json;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Children;

/// <summary>
/// Projection entries as child assignment reads them, and the publish-time duplicate rule (maintainer Q2, research R3,
/// contracts/publish-delta.md).
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

    /// <summary>The GOZ-0006 rejected body for <paramref name="problems"/>: at most 50, with <c>truncated</c>.</summary>
    public static RejectedResult Rejected(IReadOnlyList<ManifestProblem> problems) =>
        new([.. problems.Take(ManifestValidator.MaxProblems).Select(ValidationProblem.From)], problems.Count > ManifestValidator.MaxProblems);

    /// <summary>The entries of stored projection v1 bytes (the install step and re-projection).</summary>
    public static IReadOnlyList<ProjectionEntry> FromProjection(ReadOnlySpan<byte> projection)
    {
        using var document = JsonDocument.Parse(projection.ToArray());
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. entries.EnumerateArray().Select(e => new ProjectionEntry(
            e.GetProperty("kind").GetString()!,
            e.GetProperty("nativeName").GetString()!,
            e.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null))];
    }
}
