using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>
/// The reserved vocabulary (research R6, FR-007/FR-009) applied to name positions only: envelope member names, names
/// inside a format's extension mechanism and adapter-listed fields. A name matches when its normalized form, or a
/// trailing run of its words (<see cref="Words"/>), is in the list, so <c>x-gozleme-verified</c>,
/// <c>io.example/trust-level</c>, <c>x-isVerified</c> and <c>Approved</c> all match. Changing the list is a security review item.
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
    /// The normalized whole name, or any trailing run of its words, is in the list. The trailing runs make
    /// <c>x-gozleme-verified</c> match as R6's rationale requires (maintainer decision 2026-11-17, "trailing words").
    /// </summary>
    public static bool IsReserved(string name)
    {
        var words = Words(name);
        for (var i = 0; i < words.Count; i++)
        {
            if (Set.Contains(string.Concat(words.Skip(i))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The R6 normal form: the name's words (see <see cref="Words"/>) joined.</summary>
    public static string Normalize(string name) => string.Concat(Words(name));

    /// <summary>
    /// The name's words (maintainer decisions 2026-11-17, "harden all" and "built-in fold table"): look-alike characters
    /// folded to ASCII by <see cref="ReservedClaimFolds"/> (the API runs with invariant globalization, so .NET applies no
    /// NFKC); default-ignorable characters removed; namespace prefix cut at the last <c>/</c> or <c>.</c>; a leading
    /// <c>x-</c> removed; split at every character that is not a letter or digit and at camelCase boundaries; lower-cased.
    /// So <c>x-gozlemeVerified</c>, <c>x-ⅴerified</c>, <c>x-ve&#x200B;rified</c> and <c>x-trust:level</c> all match.
    /// </summary>
    internal static List<string> Words(string name)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (IsIgnorable(rune))
            {
                continue;
            }

            if (ReservedClaimFolds.Map.TryGetValue(rune.Value, out var ascii))
            {
                folded.Append(ascii);
            }
            else
            {
                folded.Append(rune.ToString());
            }
        }

        var text = folded.ToString();
        var cut = text.LastIndexOfAny(['/', '.']);
        if (cut >= 0)
        {
            text = text[(cut + 1)..];
        }

        if (text.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            var boundary = word.Length > 0 && char.IsUpper(c)
                && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1])));
            if (boundary)
            {
                Flush();
            }

            word.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString().ToLowerInvariant());
                word.Clear();
            }
        }
    }

    /// <summary>Format characters (zero-width space and joiners, soft hyphen, bidi marks) and variation selectors.</summary>
    private static bool IsIgnorable(Rune r) =>
        Rune.GetUnicodeCategory(r) == UnicodeCategory.Format || r.Value is (>= 0xFE00 and <= 0xFE0F) or 0x034F or (>= 0xE0100 and <= 0xE01EF);
}
