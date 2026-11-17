namespace Gozleme.Api.Manifests.Parsing;

/// <summary>
/// <c>SKILL.md</c> (research R3): an optional BOM, a <c>---</c> line, YAML front matter read by
/// <see cref="RestrictedYamlReader"/>, and a closing <c>---</c> line; otherwise <c>parse.front-matter</c> at line 1.
/// The body after the closing line is not parsed; its lines are exposed with their line numbers for the secret scan.
/// </summary>
internal static class SkillMarkdownReader
{
    public static SkillDocument Read(string text, string part)
    {
        RestrictedYamlReader.CheckControlCharacters(text, part);
        var lines = SplitLines(text.StartsWith('﻿') ? text[1..] : text);
        if (lines.Count == 0 || lines[0].Text != "---")
        {
            throw FrontMatter(part);
        }

        var close = lines.FindIndex(1, l => l.Text == "---");
        if (close < 0)
        {
            throw FrontMatter(part);
        }

        var yaml = string.Join("\n", lines.Skip(1).Take(close - 1).Select(l => l.Text));
        var frontMatter = RestrictedYamlReader.Read(yaml, part, lineOffset: 1)
            ?? ManifestNode.Object(string.Empty, 2, 1, []);
        return new SkillDocument(frontMatter, [.. lines.Skip(close + 1)]);
    }

    private static ValidationStop FrontMatter(string part) => new(ManifestRules.ParseFrontMatter, ProblemLocation.At(part, 1, 1));

    /// <summary>Lines split on LF, CR and CRLF, numbered from 1, without their terminators.</summary>
    private static List<BodyLine> SplitLines(string text)
    {
        var lines = new List<BodyLine>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\n' or '\r'))
            {
                continue;
            }

            lines.Add(new BodyLine(lines.Count + 1, text[start..i]));
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        return lines;
    }
}

/// <summary>A parsed <c>SKILL.md</c>: the front matter tree and the unparsed body lines.</summary>
internal sealed record SkillDocument(ManifestNode FrontMatter, IReadOnlyList<BodyLine> Body);

internal sealed record BodyLine(int Number, string Text);
