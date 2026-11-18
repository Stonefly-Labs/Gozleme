using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Revisions.Signing;

/// <summary>A newly created key: public parts for the row, and the wrapped seed. No plaintext private material.</summary>
internal sealed record NewSigningKey(byte[] PublicKey, string KeyId, byte[] EncryptedPrivateKey)
{
    public override string ToString() => $"{nameof(NewSigningKey)} {{ {nameof(KeyId)} = {KeyId} }}";
}

/// <summary>
/// The single place private signing material exists (GOZ-0007 research R6, plan "Key boundary"). Each publisher's
/// 32-byte Ed25519 seed is wrapped with AES-256-GCM under the key-encryption key (KEK) as
/// <c>0x01 ‖ nonce(12) ‖ ciphertext(32) ‖ tag(16)</c>, with associated data
/// <c>"gozleme.signing-key.v1" ‖ publisher UUID bytes ‖ UTF-8 key ID</c>, so a blob moved to another publisher or key
/// does not decrypt. An absent, malformed or wrong KEK makes every operation return false (<c>signing_unavailable</c>);
/// it never throws key material, and plaintext seeds are zeroed after their one use. Injected only into the
/// publication path and the operator succession command.
/// </summary>
internal sealed class SigningKeyVault
{
    public const byte BlobVersion = 1;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int BlobLength = 1 + NonceLength + Ed25519Signer.SeedLength + TagLength;

    private static readonly byte[] Context = Encoding.ASCII.GetBytes("gozleme.signing-key.v1");

    private readonly byte[]? _kek;

    public SigningKeyVault(IOptions<SigningOptions> options)
    {
        _kek = Parse(options.Value.KeyEncryptionKey);
    }

    /// <summary>Whether a usable KEK is configured (it may still be the wrong one for existing keys).</summary>
    public bool IsConfigured => _kek is not null;

    /// <summary>Creates a key for <paramref name="publisherId"/>; false when no usable KEK is configured.</summary>
    public bool TryCreateKey(Guid publisherId, out NewSigningKey? key)
    {
        key = null;
        if (_kek is null)
        {
            return false;
        }

        var (seed, publicKey) = Ed25519Signer.Create();
        try
        {
            var keyId = Ed25519Signer.KeyId(publicKey);
            var blob = new byte[BlobLength];
            blob[0] = BlobVersion;
            var nonce = blob.AsSpan(1, NonceLength);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_kek, TagLength);
            aes.Encrypt(nonce, seed, blob.AsSpan(1 + NonceLength, Ed25519Signer.SeedLength), blob.AsSpan(BlobLength - TagLength), AssociatedData(publisherId, keyId));
            key = new NewSigningKey(publicKey, keyId, blob);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Signs with the key row's private part; false when it cannot be decrypted or does not match the row.</summary>
    public bool TrySign(PublisherSigningKey key, ReadOnlySpan<byte> data, out byte[]? signature)
    {
        signature = null;
        var seed = Unwrap(key);
        if (seed is null)
        {
            return false;
        }

        try
        {
            signature = Ed25519Signer.Sign(seed, data);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Whether the configured KEK decrypts the key row (operator <c>--undecryptable</c>).</summary>
    public bool CanDecrypt(PublisherSigningKey key)
    {
        var seed = Unwrap(key);
        if (seed is not null)
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        return seed is not null;
    }

    public override string ToString() => $"{nameof(SigningKeyVault)} {{ {nameof(IsConfigured)} = {IsConfigured} }}";

    private byte[]? Unwrap(PublisherSigningKey key)
    {
        var blob = key.EncryptedPrivateKey;
        if (_kek is null || blob is not { Length: BlobLength } || blob[0] != BlobVersion)
        {
            return null;
        }

        var seed = new byte[Ed25519Signer.SeedLength];
        try
        {
            using var aes = new AesGcm(_kek, TagLength);
            aes.Decrypt(blob.AsSpan(1, NonceLength), blob.AsSpan(1 + NonceLength, Ed25519Signer.SeedLength), blob.AsSpan(BlobLength - TagLength), seed,
                AssociatedData(key.PublisherId, key.KeyId));
            // The seed must still be the row's key: a blob that decrypts but names another public key is refused.
            if (CryptographicOperations.FixedTimeEquals(Ed25519Signer.PublicKeyOf(seed), key.PublicKey))
            {
                return seed;
            }
        }
        catch (CryptographicException)
        {
            // Wrong KEK, or a blob moved to another publisher or key: no detail, no material.
        }

        CryptographicOperations.ZeroMemory(seed);
        return null;
    }

    private static byte[] AssociatedData(Guid publisherId, string keyId)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        var data = new byte[Context.Length + 16 + keyIdBytes.Length];
        Context.CopyTo(data, 0);
        publisherId.TryWriteBytes(data.AsSpan(Context.Length, 16), bigEndian: true, out _);
        keyIdBytes.CopyTo(data, Context.Length + 16);
        return data;
    }

    /// <summary>Base64 of exactly 32 bytes, or null.</summary>
    private static byte[]? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var buffer = new byte[33];
        try
        {
            return Convert.TryFromBase64String(value.Trim(), buffer, out var written) && written == 32 ? buffer[..32] : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
