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
        foreach (var node in tree.Descendants())
        {
            if (node is { IsString: true, Text: { } text } && !node.Pointer.Equals(skipped) && SecretPatterns.IsMatch(text))
            {
                yield return new ManifestProblem(ManifestRules.SecretPattern, ProblemLocation.At(part, node.Pointer));
            }

            if (node.Members.Any(m => SecretPatterns.IsMatch(m.Key)))
            {
                yield return new ManifestProblem(ManifestRules.SecretName, ProblemLocation.At(part, node.Pointer));
            }
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
