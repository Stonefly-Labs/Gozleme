using System.Diagnostics;
using Gozleme.Api.Revisions.Signing;
using NSec.Cryptography;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>
/// GOZ-0007 T002 (research R1, plan "Vendoring risk"): the "verify at vendoring" facts for NSec and libsodium, kept as
/// a permanent smoke test. A failure here is a stop-rule blocker for maintainer review, never a cue to switch library,
/// algorithm or framing (P1, P2).
/// </summary>
public sealed class Ed25519VendoringTests
{
    [Fact]
    public void The_RFC_8032_test_1_vector_signs_byte_for_byte_and_verifies()
    {
        Assert.True(SigningSelfTest.Run());
    }

    [Fact]
    public void A_raw_32_byte_seed_imports_exports_and_signs_without_export_permission()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var seed = SigningSelfTest.Rfc8032Test1Seed;

        // Creation: the seed may leave the key once, to be wrapped under the KEK.
        using (var creating = Key.Import(algorithm, seed, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }))
        {
            Assert.Equal(seed, creating.Export(KeyBlobFormat.RawPrivateKey));
            Assert.Equal(32, creating.PublicKey.Export(KeyBlobFormat.RawPublicKey).Length);
        }

        // Signing: the unwrapped seed is imported with no export policy, so it cannot leave the key again.
        using var signing = Key.Import(algorithm, seed, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
        Assert.Throws<InvalidOperationException>(() => signing.Export(KeyBlobFormat.RawPrivateKey));
        Assert.Equal(SigningSelfTest.Rfc8032Test1PublicKey, signing.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        Assert.Equal(SigningSelfTest.Rfc8032Test1Signature, algorithm.Sign(signing, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_fresh_key_round_trips_through_its_raw_seed()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        Assert.Equal(32, seed.Length);
        using var again = Key.Import(algorithm, seed, KeyBlobFormat.RawPrivateKey);
        Assert.Equal(key.PublicKey.Export(KeyBlobFormat.RawPublicKey), again.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    [Fact]
    public void The_public_key_exports_as_RFC_8410_SubjectPublicKeyInfo_PEM_that_openssl_accepts()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, SigningSelfTest.Rfc8032Test1PublicKey, KeyBlobFormat.RawPublicKey);
        var pem = System.Text.Encoding.ASCII.GetString(publicKey.Export(KeyBlobFormat.PkixPublicKeyText));
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem, StringComparison.Ordinal);

        // RFC 8410: SPKI DER is the 12-byte Ed25519 prefix followed by the 32 raw bytes.
        var der = publicKey.Export(KeyBlobFormat.PkixPublicKey);
        Assert.Equal("302a300506032b6570032100", Convert.ToHexStringLower(der[..12]));
        Assert.Equal(SigningSelfTest.Rfc8032Test1PublicKey, der[12..]);

        if (OpenSsl() is not { } openssl)
        {
            return; // the PEM shape is proven above; openssl is an extra independent check where installed
        }

        var file = Path.Combine(Path.GetTempPath(), $"gozleme-ed25519-{Guid.NewGuid():N}.pem");
        try
        {
            File.WriteAllText(file, pem);
            var start = new ProcessStartInfo(openssl, ["pkey", "-pubin", "-in", file, "-noout", "-text"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("ED25519", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string? OpenSsl()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "openssl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
