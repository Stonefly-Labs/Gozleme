using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Gozleme.Api.Revisions.Signing;

/// <summary>
/// Ed25519 (RFC 8032, pure, no pre-hash) over NSec (maintainer P1, research R1, R5). A private seed is imported for
/// one operation with no export policy and disposed at once; callers zero the seed buffers they own. Public keys are
/// 32 raw bytes; the key ID is <c>sha256:</c> + lowercase hex of those bytes, so anyone can recompute it (A2).
/// </summary>
internal static class Ed25519Signer
{
    public const string Algorithm = "Ed25519";
    public const int SeedLength = 32;
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;

    /// <summary>A fresh key: its 32-byte seed (the caller wraps it, then zeroes it) and its raw public key.</summary>
    public static (byte[] Seed, byte[] PublicKey) Create()
    {
        using var key = Key.Create(Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.Export(KeyBlobFormat.RawPrivateKey), key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public static byte[] PublicKeyOf(ReadOnlySpan<byte> seed)
    {
        using var key = Key.Import(Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> data)
    {
        using var key = Key.Import(Ed25519, seed, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
        return Ed25519.Sign(key, data);
    }

    /// <summary>False for a malformed public key or signature as well as for a signature that does not verify.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        signature.Length == SignatureLength
        && PublicKey.TryImport(Ed25519, publicKey, KeyBlobFormat.RawPublicKey, out var key)
        && Ed25519.Verify(key!, data, signature);

    public static string KeyId(ReadOnlySpan<byte> publicKey) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(publicKey));

    /// <summary>SubjectPublicKeyInfo PEM (RFC 8410), as <c>openssl pkey -pubin</c> and Node <c>crypto</c> read it.</summary>
    public static string Pem(ReadOnlySpan<byte> publicKey) =>
        Encoding.ASCII.GetString(PublicKey.Import(Ed25519, publicKey, KeyBlobFormat.RawPublicKey).Export(KeyBlobFormat.PkixPublicKeyText));
}
