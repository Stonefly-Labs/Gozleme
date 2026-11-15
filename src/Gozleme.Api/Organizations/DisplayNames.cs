namespace Gozleme.Api.Organizations;

/// <summary>Organization display names (FR-002): trimmed, 1–100 characters, no control characters, not unique.</summary>
internal static class DisplayNames
{
    public const string LengthError = "Enter a name of 1–100 characters.";
    public const string ControlError = "The name can't contain control characters.";

    /// <summary>The trimmed name, or a field error.</summary>
    public static (string? Name, string? Error) Validate(string? raw)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Organization.MaxDisplayNameLength)
        {
            return (null, LengthError);
        }

        return name.Any(char.IsControl) ? (null, ControlError) : (name, null);
    }
}
