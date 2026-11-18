using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>
/// References (research R10, FR-006): nothing is ever fetched. <c>$ref</c>/<c>$dynamicRef</c> not starting with
/// <c>#</c> is <c>ref.external</c>; an internal reference must resolve within its scope (the document, or an embedded
/// schema such as an MCP <c>inputSchema</c>) or it is <c>ref.unresolved</c>; a <c>$schema</c> or <c>jsonSchemaDialect</c>
/// outside the bundled dialects is <c>ref.dialect</c>. Example, default, enum and const values are data and are not
/// inspected. Informational URLs are never rejected for being URLs.
/// </summary>
internal static class ExternalReferences
{
    public const string OasDialect = "https://spec.openapis.org/oas/3.1/dialect/base";

    private static readonly string[] Dialects =
    [
        "http://json-schema.org/draft-07/schema#", "http://json-schema.org/draft-07/schema",
        "https://json-schema.org/draft/2019-09/schema", "https://json-schema.org/draft/2020-12/schema",
    ];

    /// <summary>Checks references in <paramref name="scope"/>; sample data is skipped by position (<see cref="DataPositions"/>, T083).</summary>
    public static IEnumerable<ManifestProblem> Check(ManifestNode scope, string part, bool allowOasDialect, DataPositions positions)
    {
        var problems = new List<ManifestProblem>();
        Walk(scope, PositionKind.Object, scope, part, allowOasDialect, positions, problems);
        return problems;
    }

    private static void Walk(
        ManifestNode node, PositionKind kind, ManifestNode scope, string part, bool allowOasDialect, DataPositions positions, List<ManifestProblem> problems)
    {
        foreach (var (name, value, childKind) in positions.Members(node, kind))
        {
            if (name is "$ref" or "$dynamicRef" && value is { IsString: true, Text: { } reference })
            {
                if (!reference.StartsWith('#'))
                {
                    problems.Add(new ManifestProblem(ManifestRules.RefExternal, ProblemLocation.At(part, value.Pointer)));
                }
                else if (!Resolves(scope, reference[1..]))
                {
                    problems.Add(new ManifestProblem(ManifestRules.RefUnresolved, ProblemLocation.At(part, value.Pointer)));
                }
            }
            else if (name is "$schema" or "jsonSchemaDialect" && value is { IsString: true, Text: { } dialect }
                && !Dialects.Contains(dialect, StringComparer.Ordinal) && !(allowOasDialect && dialect == OasDialect))
            {
                problems.Add(new ManifestProblem(ManifestRules.RefDialect, ProblemLocation.At(part, value.Pointer)));
            }

            Walk(value, childKind, scope, part, allowOasDialect, positions, problems);
        }

        foreach (var item in node.Items)
        {
            Walk(item, PositionKind.Object, scope, part, allowOasDialect, positions, problems);
        }
    }

    /// <summary>An empty fragment, a JSON Pointer fragment that resolves, or a plain name declared as an anchor in the scope.</summary>
    private static bool Resolves(ManifestNode scope, string fragment)
    {
        fragment = Uri.UnescapeDataString(fragment);
        if (fragment.Length == 0)
        {
            return true;
        }

        if (!fragment.StartsWith('/'))
        {
            return scope.Descendants().Any(n => n.StringOf("$anchor") == fragment || n.StringOf("$dynamicAnchor") == fragment);
        }

        ManifestNode? current = scope;
        foreach (var raw in fragment[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                { IsObject: true } => current[segment],
                { IsArray: true } when int.TryParse(segment, out var i) && i >= 0 && i < current.Items.Count => current.Items[i],
                _ => null,
            };
            if (current is null)
            {
                return false;
            }
        }

        return true;
    }
}
