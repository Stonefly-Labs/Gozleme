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

        problems.AddRange(SecretFields.Fields(adapter.SecretFields(tree), ManifestPart.Native));
        problems.AddRange(SecretFields.Patterns(tree, ManifestPart.Native));
        if (skill is not null)
        {
            problems.AddRange(SecretFields.Body(skill.Body, ManifestPart.Native));
        }

        foreach (var scope in adapter.ReferenceScopes(tree))
        {
            problems.AddRange(ExternalReferences.Check(scope, ManifestPart.Native, allowOasDialect: adapter is OpenApiAdapter));
        }
    }

    /// <summary>
    /// Drops every problem whose pointer has a credential-shaped segment: the <c>secret.name</c> problem at its parent
    /// already reports it, and a pointer must never carry a matched credential (contracts/manifest-validation.md).
    /// </summary>
    public static List<ManifestProblem> WithoutSecretPointers(List<ManifestProblem> problems)
    {
        // Memoized per pointer step, so the patterns run once per distinct segment, not once per problem (T074).
        var carries = new Dictionary<JsonPointer, bool>(ReferenceEqualityComparer.Instance);
        bool Carries(JsonPointer at)
        {
            if (at.Parent is null)
            {
                return false;
            }

            if (!carries.TryGetValue(at, out var result))
            {
                carries[at] = result = SecretPatterns.IsMatch(at.Segment) || Carries(at.Parent);
            }

            return result;
        }

        return [.. problems.Where(p => p.Location?.Path is not { } path || !Carries(path))];
    }
}
