using System.Security.Cryptography;
using System.Text;

namespace Gozleme.Api.Organizations;

/// <summary>
/// Invitation codes (research R5): 160 random bits as 32 Crockford base32 characters, shown in
/// dash-separated groups of four. Input is case-insensitive, ignores dashes and whitespace and maps
/// the Crockford aliases. Only a SHA-256 of the normalized code is ever stored.
/// </summary>
internal static class InvitationCodes
{
    public const int Length = 32;
    public const int MaxInputLength = 64;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        var chars = new StringBuilder(Length + (Length / 4) - 1);
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                if (chars.Length > 0 && (chars.Length + 1) % 5 == 0)
                {
                    chars.Append('-');
                }

                chars.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return chars.ToString();
    }

    /// <summary>The canonical 32-character form, or null when the input cannot be a code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInputLength)
        {
            return null;
        }

        var result = new StringBuilder(Length);
        foreach (var raw in input)
        {
            if (raw == '-' || char.IsWhiteSpace(raw))
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return null;
            }

            result.Append(c);
        }

        return result.Length == Length ? result.ToString() : null;
    }

    public static byte[] Hash(string normalized) => SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
}
