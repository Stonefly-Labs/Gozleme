using System.Collections.Frozen;

namespace Gozleme.Api.Publishers;

/// <summary>
/// The one namespace rule for publisher handles and capability names (GOZ-0005 FR-016–FR-018, research R7,
/// contracts/namespace.md): <c>^[a-z][a-z0-9]*(-[a-z0-9]+)*$</c>, handles 2–39 characters, names 2–64.
/// Ordinal <c>char</c> range checks only: input is never trimmed, case-folded, normalized or transliterated,
/// so a look-alike is rejected rather than mapped. The table <c>CHECK</c>s repeat the same rule.
/// </summary>
internal static class NamespaceRules
{
    public const int MinLength = 2;
    public const int MaxHandleLength = 39;
    public const int MaxCapabilityNameLength = 64;

    /// <summary>The rule as a PostgreSQL regular expression, for the table <c>CHECK</c>s.</summary>
    public const string Pattern = "^[a-z][a-z0-9]*(-[a-z0-9]+)*$";

    /// <summary>Handles nobody may register or reserve (FR-018); separate from the operator's reservations.</summary>
    public static readonly FrozenSet<string> ReservedHandles = new[]
    {
        "admin", "api", "capabilities", "capability", "gozleme", "operator", "publisher", "publishers",
        "registry", "root", "security", "support", "system", "www",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsValidHandle(string? value) =>
        Matches(value, MaxHandleLength) && !ReservedHandles.Contains(value!);

    public static bool IsValidCapabilityName(string? value) => Matches(value, MaxCapabilityNameLength);

    private static bool Matches(string? value, int maxLength)
    {
        if (value is null || value.Length < MinLength || value.Length > maxLength || !IsLetter(value[0]) || value[^1] == '-')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '-' ? value[i - 1] == '-' : !IsLetter(c) && !IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(char c) => c is >= 'a' and <= 'z';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>The <c>400 validation_failed</c> field messages (contracts/publishers-api.openapi.yaml).</summary>
    public static class RuleText
    {
        public const string Handle =
            "Use 2–39 lowercase letters, digits and single hyphens, starting with a letter and not ending with a hyphen. Reserved handles can't be used.";

        public const string CapabilityName =
            "Use 2–64 lowercase letters, digits and single hyphens, starting with a letter and not ending with a hyphen.";
    }
}
