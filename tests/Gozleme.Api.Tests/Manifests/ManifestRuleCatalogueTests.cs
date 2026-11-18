using System.Text.RegularExpressions;
using Gozleme.Api.Manifests;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 FR-012 (research R9): <see cref="ManifestRules"/> is exactly the contract's rule → category table, the
/// categories are the closed 11 in contract order, and every message is a constant with no placeholder.
/// </summary>
public sealed partial class ManifestRuleCatalogueTests
{
    private static string Contract() => File.ReadAllText(Path.Combine(ManifestSchemaPinTests.RepositoryRoot(), "docs/specs/006-typed-manifests/contracts/manifest-validation.md"));

    [Fact]
    public void The_catalogue_equals_the_contract_table()
    {
        var text = Contract();
        var table = text[text.IndexOf("## Rule catalogue", StringComparison.Ordinal)..];
        var expected = TableRow().Matches(table)
            .SelectMany(m => RuleName().Matches(m.Groups["rules"].Value).Select(r => $"{r.Groups[1].Value} {m.Groups["category"].Value}"))
            .ToList();
        Assert.Equal(38, expected.Count);
        Assert.Equal(expected, ManifestRules.All.Select(r => $"{r.Id} {ManifestRules.Name(r.Category)}"));
    }

    [Fact]
    public void The_categories_are_the_closed_eleven_in_contract_order()
    {
        var text = Contract();
        var line = text[(text.IndexOf("## Categories", StringComparison.Ordinal) + 1)..].Split('\n').Skip(2).TakeWhile(l => l.Length > 0);
        var expected = RuleName().Matches(string.Join(' ', line)).Select(m => m.Groups[1].Value);
        Assert.Equal(expected, Enum.GetValues<ProblemCategory>().Select(ManifestRules.Name));
    }

    [Fact]
    public void Every_message_is_a_fixed_sentence_without_placeholders()
    {
        foreach (var rule in ManifestRules.All)
        {
            Assert.DoesNotContain("{", rule.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("%", rule.Message, StringComparison.Ordinal);
            Assert.EndsWith(".", rule.Message, StringComparison.Ordinal);
        }

        Assert.Equal(ManifestRules.All.Count, ManifestRules.All.Select(r => r.Message).Distinct().Count());
    }

    [GeneratedRegex(@"^\| (?<rules>`[^|]+) \| `(?<category>[a-z_]+)` \|$", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    [GeneratedRegex("`([a-z0-9_.-]+)`")]
    private static partial Regex RuleName();
}
