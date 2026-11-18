using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Manifests.Parsing;
using Gozleme.Api.Manifests.Rules;

namespace Gozleme.Api.Manifests;

/// <summary>
/// The format-independent rules of stages 3 and 5 (research R6, R7, R10): reserved names, inline secrets and
/// external references, over the envelope tree and over the native tree at each adapter's positions.
/// </summary>
internal static class ManifestRuleChecks
{
    /// <summary>Stage 3: every envelope member name against the vocabulary; secret patterns over every value except the native document.</summary>
    public static void Envelope(ManifestNode tree, List<ManifestProblem> problems)
    {
        foreach (var node in tree.Descendants())
        {
            foreach (var (name, value) in node.Members.Where(m => ReservedClaims.IsReserved(m.Key)))
            {
                problems.Add(new ManifestProblem(ManifestRules.ReservedVocabulary, ProblemLocation.At(ManifestPart.Envelope, value.Pointer)));
            }
        }

        problems.AddRange(SecretFields.Patterns(tree, ManifestPart.Envelope, skip: "/native/document"));
    }

    /// <summary>Stage 5: the adapter's reserved name positions, secret-bearing fields, the pattern list and references.</summary>
    public static void Native(IProtocolAdapter adapter, ManifestNode tree, SkillDocument? skill, List<ManifestProblem> problems)
    {
        foreach (var position in adapter.ReservedNamePositions(tree).Where(p => ReservedClaims.IsReserved(p.Name)))
        {
            problems.Add(new ManifestProblem(ManifestRules.ReservedVocabulary, ProblemLocation.At(ManifestPart.Native, position.Pointer)));
        }

        // A reserved name in a closed A2A, OpenAPI or AsyncAPI object: the schema reports it as unknown, and it is a
        // reserved claim as well (maintainer decision 2026-11-17, "follow the contract", T086). Fields the protocol
        // defines are never unknown, so native fields such as `deprecated` stay untouched (A1).
        if (adapter is A2aAdapter or OpenApiAdapter or AsyncApiAdapter)
        {
            var closed = problems.Where(p => p.Rule == ManifestRules.NativeUnknownField
                && p.Location?.Path is { Parent: not null } path && ReservedClaims.IsReserved(path.Segment)).ToList();
            problems.AddRange(closed.Select(p => new ManifestProblem(ManifestRules.ReservedVocabulary, p.Location)));
        }

        problems.AddRange(SecretFields.Fields(adapter.SecretFields(tree), ManifestPart.Native));
        problems.AddRange(SecretFields.Patterns(tree, ManifestPart.Native));
        if (skill is not null)
        {
            problems.AddRange(SecretFields.Body(skill.Body, ManifestPart.Native));
        }

        foreach (var scope in adapter.ReferenceScopes(tree))
        {
            var positions = adapter switch
            {
                OpenApiAdapter => DataPositions.OpenApi,
                AsyncApiAdapter => DataPositions.AsyncApi,
                _ => DataPositions.JsonSchema,
            };
            problems.AddRange(ExternalReferences.Check(scope, ManifestPart.Native, allowOasDialect: adapter is OpenApiAdapter, positions));
        }
    }

    /// <summary>
    /// Drops every problem whose pointer has a credential-shaped segment, or whose RFC 6901 text matches the patterns
    /// once its names are joined (T091): the <c>secret.name</c> problem at its parent
    /// already reports it, and a pointer must never carry a matched credential (contracts/manifest-validation.md).
    /// </summary>
    public static List<ManifestProblem> WithoutSecretPointers(List<ManifestProblem> problems)
    {
        // Memoized per pointer step and per segment text, so the patterns run once per distinct segment, not once per
        // problem: pointers parsed from schema results share no steps (T074, T084).
        var carries = new Dictionary<JsonPointer, bool>(ReferenceEqualityComparer.Instance);
        var matched = new Dictionary<string, bool>(StringComparer.Ordinal);
        var joined = new SecretFields.LocationScan();
        bool Carries(JsonPointer at)
        {
            if (at.Parent is null)
            {
                return false;
            }

            if (!carries.TryGetValue(at, out var result))
            {
                if (!matched.TryGetValue(at.Segment, out var own))
                {
                    matched[at.Segment] = own = SecretPatterns.IsMatch(at.Segment);
                }

                carries[at] = result = own || Carries(at.Parent) || SecretFields.LocationMatches(at, joined);
            }

            return result;
        }

        return [.. problems.Where(p => p.Location?.Path is not { } path || !Carries(path))];
    }
}
