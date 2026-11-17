using System.Collections.Frozen;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>
/// The reserved vocabulary (research R6, FR-007/FR-009) applied to name positions only: envelope member names, names
/// inside a format's extension mechanism and adapter-listed fields. A name matches when its normalized form
/// (lower-case; namespace prefix removed up to the last <c>/</c> or <c>.</c>; a leading <c>x-</c> removed; <c>-</c>,
/// <c>_</c> and <c>.</c> removed), or a trailing run of its words, is in the list, so <c>x-gozleme-verified</c>,
/// <c>io.example/trust-level</c> and <c>Approved</c> all match. Changing the list is a security review item.
/// </summary>
internal static class ReservedClaims
{
    public static readonly IReadOnlyList<string> Vocabulary =
    [
        "signature", "signatures", "sig", "jws", "attestation", "attestations", "provenance", "trust", "trusted", "trustlevel",
        "verified", "verification", "approved", "approval", "certified", "delegation", "delegations", "delegationchain",
        "delegatedby", "transfer", "transfers", "transferchain", "transferredfrom", "visibility", "visible", "grant", "grants",
        "sharing", "sharedwith", "acl", "owner", "ownedby", "publisher", "publishedby", "organization", "status", "lifecycle",
        "deprecated", "revoked", "withdrawn", "yanked",
    ];

    private static readonly FrozenSet<string> Set = Vocabulary.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The normalized whole name, or any trailing run of its <c>-</c>/<c>_</c>-separated words, is in the list. The
    /// trailing runs make <c>x-gozleme-verified</c> match as R6's rationale requires, which the whole-name form alone
    /// (<c>gozlemeverified</c>) would not (agent choice, disclosed to the maintainer for veto).
    /// </summary>
    public static bool IsReserved(string name)
    {
        var words = Words(name);
        for (var i = 0; i < words.Length; i++)
        {
            if (Set.Contains(string.Concat(words[i..])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>R6 normalization: lower-case, namespace prefix and leading <c>x-</c> removed, then <c>-</c>, <c>_</c> and <c>.</c> removed.</summary>
    public static string Normalize(string name) => string.Concat(Words(name));

    private static string[] Words(string name)
    {
        var lower = name.ToLowerInvariant();
        var cut = lower.LastIndexOfAny(['/', '.']);
        if (cut >= 0)
        {
            lower = lower[(cut + 1)..];
        }

        if (lower.StartsWith("x-", StringComparison.Ordinal))
        {
            lower = lower[2..];
        }

        return lower.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
    }
}
