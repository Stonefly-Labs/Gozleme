using System.Text.RegularExpressions;

namespace Gozleme.Api.Manifests.Rules;

/// <summary>
/// Secret pattern list v1 (contracts/secret-patterns.md, research R7, clarify Q3): ordered, deterministic, no entropy
/// heuristic. Each pattern is compiled <c>CultureInvariant | NonBacktracking</c> with a 100 ms timeout, and a timeout
/// counts as a match (fail closed). Changing the list is a contract change and a security review item.
/// </summary>
internal static partial class SecretPatterns
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static readonly IReadOnlyList<(string Id, Regex Pattern)> All =
    [
        ("pem-private-key", new(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----", Options, Timeout)),
        ("auth-header", new(@"(?i)\b(?:bearer|basic|digest)\s+[A-Za-z0-9._~+/=-]{12,}", Options, Timeout)),
        ("jwt", new(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{0,}", Options, Timeout)),
        ("url-userinfo", new(@"(?i)\b[a-z][a-z0-9+.-]{1,31}://[^\s/:@]{1,256}:[^\s/@]{1,256}@", Options, Timeout)),
        ("aws-access-key-id", new(@"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b", Options, Timeout)),
        ("github-token", new(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})", Options, Timeout)),
        ("gitlab-token", new(@"\bglpat-[A-Za-z0-9_-]{20,}", Options, Timeout)),
        ("slack-token", new(@"\bxox[abposr]-[A-Za-z0-9-]{10,}", Options, Timeout)),
        ("stripe-key", new(@"\b(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}", Options, Timeout)),
        ("google-api-key", new(@"\bAIza[0-9A-Za-z_-]{35}\b", Options, Timeout)),
        ("anthropic-key", new(@"\bsk-ant-[A-Za-z0-9_-]{20,}", Options, Timeout)),
        ("openai-key", new(@"\bsk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}", Options, Timeout)),
        ("npm-token", new(@"\bnpm_[A-Za-z0-9]{36}\b", Options, Timeout)),
        ("azure-connection-secret", new(@"(?i)(?:AccountKey|SharedAccessKey|SharedAccessSignature)=[A-Za-z0-9+/%=&:-]{16,}", Options, Timeout)),
    ];

    /// <summary>The first pattern that matches and where its match starts, or null. A timeout counts as a match at index 0.</summary>
    public static (string Id, int Index)? Match(string value) => Match(value, All);

    internal static (string Id, int Index)? Match(string value, IReadOnlyList<(string Id, Regex Pattern)> patterns)
    {
        foreach (var (id, pattern) in patterns)
        {
            try
            {
                if (pattern.Match(value) is { Success: true } match)
                {
                    return (id, match.Index);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return (id, 0);
            }
        }

        return null;
    }

    public static bool IsMatch(string value) => Match(value) is not null;

    /// <summary>
    /// The patterns checked on a location's joined RFC 6901 text: those that need member names joined to exist, with a
    /// fixed part every match contains (maintainer decisions 2026-11-17, "reject and trim" and "drop the header
    /// pattern", T091/T092). <c>auth-header</c> also allows <c>/</c> but is checked only within single names and
    /// values, because honest names such as <c>basic info</c> with a child would match once joined. A test fails if
    /// another pattern allowing <c>/</c> or <c>~</c> is not listed here or in <see cref="NotJoined"/>.
    /// </summary>
    internal static readonly IReadOnlyList<(string Id, Regex Pattern)> Joined = [.. All.Where(p => p.Id is "url-userinfo" or "azure-connection-secret")];

    /// <summary>Patterns that allow <c>/</c> or <c>~</c> but are deliberately not checked on joined locations.</summary>
    internal static readonly string[] NotJoined = ["auth-header"];

    /// <summary>
    /// The fixed parts of <see cref="Joined"/> that sit inside one name, matched exactly as the patterns match them
    /// (<c>(?i)</c>, culture-invariant, so the Kelvin sign U+212A counts as <c>k</c>). <c>://</c> is the other fixed
    /// part; in a location it arises only from a name ending in <c>:</c> followed by an empty name.
    /// </summary>
    internal static readonly Regex JoinedAnchors = new(@"(?i)(?:AccountKey|SharedAccessKey|SharedAccessSignature)=", Options, Timeout);

    /// <summary>
    /// The most characters of a <see cref="Joined"/> match that can lie before the name completing it: a whole
    /// <c>url-userinfo</c> match is at most 32 + 3 + 256 + 1 + 256 + 1 = 549 characters, and an
    /// <c>azure-connection-secret</c> match that did not already match earlier has at most
    /// <c>SharedAccessSignature=</c> plus 15 characters there. Changing either pattern means revisiting this.
    /// </summary>
    internal const int JoinedReach = 549;

    /// <summary>Whether a location's joined text matches a <see cref="Joined"/> pattern.</summary>
    public static bool IsJoinedMatch(string location) => Match(location, Joined) is not null;


    /// <summary>
    /// A value that is never flagged in a secret-bearing field: empty, a secret reference <c>secretref:&lt;provider&gt;/&lt;key&gt;</c>
    /// (agent choice A2) or an MCP placeholder <c>{NAME}</c>.
    /// </summary>
    public static bool IsAccepted(string value) => value.Length == 0 || SecretReference().IsMatch(value) || Placeholder().IsMatch(value);

    [GeneratedRegex("^secretref:[a-z][a-z0-9-]{0,31}/[A-Za-z0-9._/-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();

    [GeneratedRegex("^\\{[A-Za-z_][A-Za-z0-9_]*\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
