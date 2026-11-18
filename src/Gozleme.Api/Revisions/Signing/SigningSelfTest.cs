using NSec.Cryptography;

namespace Gozleme.Api.Revisions.Signing;

/// <summary>
/// GOZ-0007 research R1 ("verify at vendoring"): proves that the native libsodium behind NSec loads in this process and
/// signs and verifies the RFC 8032 section 7.1 TEST 1 vector byte-for-byte. Run by the unit suite and, because the
/// runtime image has no SDK, by <c>dotnet Gozleme.Api.dll signing-selftest</c> inside the built container.
/// </summary>
internal static class SigningSelfTest
{
    // RFC 8032 section 7.1, TEST 1: the empty message.
    public static readonly byte[] Rfc8032Test1Seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"u8);
    public static readonly byte[] Rfc8032Test1PublicKey = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"u8);
    public static readonly byte[] Rfc8032Test1Signature = Convert.FromHexString(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"u8);

    /// <summary>True when signing the vector gives the RFC signature, it verifies, and the public key matches.</summary>
    public static bool Run()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Import(algorithm, Rfc8032Test1Seed, KeyBlobFormat.RawPrivateKey);
        var signature = algorithm.Sign(key, ReadOnlySpan<byte>.Empty);
        return signature.AsSpan().SequenceEqual(Rfc8032Test1Signature)
            && key.PublicKey.Export(KeyBlobFormat.RawPublicKey).AsSpan().SequenceEqual(Rfc8032Test1PublicKey)
            && algorithm.Verify(key.PublicKey, ReadOnlySpan<byte>.Empty, signature);
    }

    public static async Task<int> RunCommandAsync(TextWriter stdout, TextWriter stderr)
    {
        bool passed;
        try
        {
            passed = Run();
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            await stderr.WriteLineAsync($"signing-selftest: libsodium did not load ({e.GetType().Name})");
            return 1;
        }

        await (passed ? stdout : stderr).WriteLineAsync(passed ? "signing-selftest: ok (Ed25519, RFC 8032 test 1)" : "signing-selftest: FAILED");
        return passed ? 0 : 1;
    }
}
