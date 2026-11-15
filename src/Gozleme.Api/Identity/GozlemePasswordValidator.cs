using System.Collections.Frozen;
using Microsoft.AspNetCore.Identity;

namespace Gozleme.Api.Identity;

/// <summary>
/// Password policy beyond Identity's minimum length (FR-003, NIST SP 800-63B): at most 128
/// characters, not the email or its local part, and not on the bundled common-password list.
/// No composition rules.
/// </summary>
internal sealed class GozlemePasswordValidator : IPasswordValidator<GozlemeUser>
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly Lazy<FrozenSet<string>> Common = new(LoadCommon);

    public Task<IdentityResult> ValidateAsync(UserManager<GozlemeUser> manager, GozlemeUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Task.FromResult(IdentityResult.Success); // Identity's length validator reports this
        }

        var errors = new List<IdentityError>();
        if (password.Length > MaxLength)
        {
            errors.Add(new() { Code = "PasswordTooLong", Description = $"Use at most {MaxLength} characters." });
        }

        if (IsEmailDerived(user.Email, password))
        {
            errors.Add(new() { Code = "PasswordMatchesEmail", Description = "Don't use your email address as your password." });
        }
        else if (IsCommon(password))
        {
            errors.Add(new() { Code = "PasswordTooCommon", Description = "This password is too common. Choose a less predictable one." });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
    }

    internal static bool IsCommon(string password) => Common.Value.Contains(password.ToLowerInvariant());

    private static bool IsEmailDerived(string? email, string password)
    {
        if (string.IsNullOrEmpty(email))
        {
            return false;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return string.Equals(password, email, StringComparison.OrdinalIgnoreCase)
            || (at > 0 && string.Equals(password, email[..at], StringComparison.OrdinalIgnoreCase));
    }

    private static FrozenSet<string> LoadCommon()
    {
        using var stream = typeof(GozlemePasswordValidator).Assembly.GetManifestResourceStream("Gozleme.Api.Identity.common-passwords.txt")
            ?? throw new InvalidOperationException("Common password list is not embedded.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                set.Add(line);
            }
        }

        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
