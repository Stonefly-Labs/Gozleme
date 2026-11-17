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
    /// A value that is never flagged in a secret-bearing field: empty, a secret reference <c>secretref:&lt;provider&gt;/&lt;key&gt;</c>
    /// (agent choice A2) or an MCP placeholder <c>{NAME}</c>.
    /// </summary>
    public static bool IsAccepted(string value) => value.Length == 0 || SecretReference().IsMatch(value) || Placeholder().IsMatch(value);

    [GeneratedRegex("^secretref:[a-z][a-z0-9-]{0,31}/[A-Za-z0-9._/-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();

    [GeneratedRegex("^\\{[A-Za-z_][A-Za-z0-9_]*\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
