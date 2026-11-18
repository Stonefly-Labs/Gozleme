using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>
/// Inline-secret detection (research R7, FR-010). Rule 1: a non-empty string in a secret-bearing field the adapter lists
/// is <c>secret.field</c> unless it is a secret reference or placeholder. Rule 2: pattern list v1 over every string
/// value (<c>secret.pattern</c>) and member name (<c>secret.name</c>, located at the parent so no pointer carries the
/// credential), and over a <c>SKILL.md</c> body line by line (line and column).
/// </summary>
internal static class SecretFields
{
    public static IEnumerable<ManifestProblem> Fields(IEnumerable<ManifestNode> fields, string part) =>
        fields.Where(f => f is { IsString: true, Text: { } text } && !SecretPatterns.IsAccepted(text))
            .Select(f => new ManifestProblem(ManifestRules.SecretField, ProblemLocation.At(part, f.Pointer)));

    /// <summary>Rule 2 over a tree; <paramref name="skip"/> is a pointer whose value is not scanned (the envelope's native document).</summary>
    public static IEnumerable<ManifestProblem> Patterns(ManifestNode tree, string part, string? skip = null)
    {
        var skipped = skip is null ? null : JsonPointer.Parse(skip);
        var joined = new LocationScan();
        foreach (var node in tree.Descendants())
        {
            if (node is { IsString: true, Text: { } text } && !node.Pointer.Equals(skipped) && SecretPatterns.IsMatch(text))
            {
                yield return new ManifestProblem(ManifestRules.SecretPattern, ProblemLocation.At(part, node.Pointer));
            }

            // A credential in one member name, or one that only appears once names are joined into a location
            // (`https:` → `` → `u:pw@host`): reported at the deepest parent whose own location is clean (T091).
            if (node.Members.Any(m => SecretPatterns.IsMatch(m.Key))
                || ((node.Members.Count > 0 || node.Items.Count > 0) && !LocationMatches(node.Pointer, joined)
                    && (node.Members.Any(m => LocationMatches(m.Value.Pointer, joined)) || node.Items.Any(i => LocationMatches(i.Pointer, joined)))))
            {
                yield return new ManifestProblem(ManifestRules.SecretName, ProblemLocation.At(part, node.Pointer));
            }
        }
    }

    /// <summary>
    /// Whether a location's RFC 6901 text, as it would be returned, matches the patterns (maintainer decision
    /// 2026-11-17, "reject and trim", T091). The patterns run on the whole text only beneath a name holding the fixed
    /// part of a pattern checked on joined locations (<see cref="SecretPatterns.Joined"/>, T092); elsewhere they cannot
    /// match. Array positions count as names here. Both answers are memoized per pointer in <paramref name="scan"/>.
    /// </summary>
    internal static bool LocationMatches(JsonPointer pointer, LocationScan scan)
    {
        if (pointer.Parent is null || !scan.Anchored(pointer))
        {
            return false;
        }

        // Neither joined pattern asserts anything after its match, so a match in the parent's text is one here too;
        // a new match ends in the last name and reaches back at most JoinedReach characters (T092).
        if (!scan.Matched.TryGetValue(pointer, out var match))
        {
            scan.Matched[pointer] = match = LocationMatches(pointer.Parent, scan) || SecretPatterns.IsJoinedMatch(Window(pointer));
        }

        return match;
    }

    /// <summary>
    /// The end of the pointer's RFC 6901 text: its last name plus whole earlier names until more than
    /// <see cref="SecretPatterns.JoinedReach"/> characters precede it. It starts at a <c>/</c>, as at that point in the
    /// full text, so a <c>\b</c> at a match start sees the same character.
    /// </summary>
    private static string Window(JsonPointer pointer)
    {
        var names = new List<string>();
        var reach = 0;
        for (var at = pointer; at.Parent is not null && reach <= SecretPatterns.JoinedReach; at = at.Parent)
        {
            var name = JsonPointer.Escape(at.Segment);
            names.Add(name);
            reach += at == pointer ? 0 : name.Length + 1;
        }

        names.Reverse();
        return "/" + string.Join('/', names);
    }

    /// <summary>Per-validation memo for <see cref="LocationMatches"/>.</summary>
    internal sealed class LocationScan
    {
        private readonly Dictionary<JsonPointer, bool> _anchored = new(ReferenceEqualityComparer.Instance);

        public Dictionary<JsonPointer, bool> Matched { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether the location's text contains a spanning pattern's fixed part, worked out name by name.</summary>
        public bool Anchored(JsonPointer pointer)
        {
            if (pointer.Parent is null)
            {
                return false;
            }

            if (!_anchored.TryGetValue(pointer, out var anchored))
            {
                _anchored[pointer] = anchored = Anchored(pointer.Parent)
                    || SecretPatterns.JoinedAnchors.IsMatch(pointer.Segment)
                    || (pointer.Segment.Length == 0 && pointer.Parent.Segment.EndsWith(':'));
            }

            return anchored;
        }
    }

    public static IEnumerable<ManifestProblem> Body(IEnumerable<BodyLine> lines, string part)
    {
        foreach (var line in lines)
        {
            if (SecretPatterns.Match(line.Text) is { } match)
            {
                var (_, column) = TextPositions.Of(line.Text, match.Index);
                yield return new ManifestProblem(ManifestRules.SecretPattern, ProblemLocation.At(part, line.Number, column));
            }
        }
    }

    /// <summary>
    /// YAML comments (OpenAPI/AsyncAPI YAML and SKILL.md front matter): a pattern hit is <c>secret.pattern</c> at its
    /// line and column, never the value (maintainer decision 2026-11-17, "scan raw text", T078).
    /// </summary>
    public static IEnumerable<ManifestProblem> YamlComments(string yaml, int lineOffset, string part)
    {
        var comments = RestrictedYamlReader.Comments(yaml, lineOffset, out var columns).ToList();
        for (var i = 0; i < comments.Count; i++)
        {
            if (SecretPatterns.Match(comments[i].Text) is { } match)
            {
                var (_, offset) = TextPositions.Of(comments[i].Text, match.Index);
                yield return new ManifestProblem(ManifestRules.SecretPattern, ProblemLocation.At(part, comments[i].Number, columns[i] + offset - 1));
            }
        }
    }
}
