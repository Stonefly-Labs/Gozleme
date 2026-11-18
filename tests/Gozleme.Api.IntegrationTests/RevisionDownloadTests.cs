using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Revisions;
using NSec.Cryptography;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T051 (FR-011, contracts/attestation-statement-v1.md "Downloads"): every part is an attachment with its
/// media type, <c>no-store</c> and <c>nosniff</c>; the native bytes are the submitted bytes; the digests in the
/// statement are the SHA-256 of the downloads; and the downloaded PEM verifies the downloaded statement and signature
/// outside Gözleme's own verifier (NSec from the PEM text, and <c>openssl pkeyutl -rawin</c> when the local OpenSSL has it).
/// </summary>
public sealed class RevisionDownloadTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    public static TheoryData<string> ValidSamples => [.. ManifestClient.Samples.Where(s => s.Valid).GroupBy(s => s.Family).Select(g => g.First().Id)];

    private async Task<(byte[] Bytes, HttpResponseMessage Response)> DownloadAsync(string path)
    {
        var response = await _s.Erin.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        return (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), response);
    }

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public async Task Every_download_is_the_exact_stored_part_and_verifies_independently(string sample)
    {
        var body = RevisionClient.Envelope("{{capability}}", "1.0.0", sample);
        var (capabilityId, canonical, revision) = await _s.PublishedAsync(sample: sample);
        var revisionPath = _s.Revision(capabilityId, revision.GetProperty("id").GetString()!);
        var submitted = JsonDocument.Parse(body).RootElement.GetProperty("native");
        var encoding = submitted.GetProperty("encoding").GetString()!;

        var (native, nativeResponse) = await DownloadAsync(revisionPath + "/native");
        using (nativeResponse)
        {
            Assert.Equal(RevisionEndpoints.NativeMediaType(encoding), nativeResponse.Content.Headers.ContentType?.ToString());
            Assert.Equal(Encoding.UTF8.GetBytes(submitted.GetProperty("document").GetString()!), native);
        }

        var (projection, projectionResponse) = await DownloadAsync(revisionPath + "/projection");
        var (statement, statementResponse) = await DownloadAsync(revisionPath + "/statement");
        var (signature, signatureResponse) = await DownloadAsync(revisionPath + "/signature");
        using (projectionResponse)
        using (statementResponse)
        using (signatureResponse)
        {
            Assert.Equal("application/json", projectionResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("application/json", statementResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("application/octet-stream", signatureResponse.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(64, signature.Length);
        Assert.True(CanonicalJson.IsCanonical(statement));
        var signed = JsonDocument.Parse(statement).RootElement;
        Assert.Equal(canonical, signed.GetProperty("capability").GetString());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(native)), signed.GetProperty("native").GetProperty("digest").GetString());
        Assert.Equal(native.Length, signed.GetProperty("native").GetProperty("byteLength").GetInt32());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(projection)), signed.GetProperty("projection").GetProperty("digest").GetString());
        Assert.Equal(revision.GetProperty("projection").GetRawText(), JsonDocument.Parse(projection).RootElement.GetRawText());

        // The public key: an SPKI PEM whose raw key hashes to the statement's key ID and verifies the signature.
        var keyId = signed.GetProperty("key").GetProperty("id").GetString()!;
        var (_, keys) = await RevisionClient.GetAsync(_s.Erin, RevisionClient.Keys(_s.OrgA, _s.PublisherId));
        var keyUuid = keys.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("keyId").GetString() == keyId).GetProperty("id").GetString();
        var (pem, pemResponse) = await DownloadAsync($"{RevisionClient.Keys(_s.OrgA, _s.PublisherId)}/{keyUuid}/public-key");
        using (pemResponse)
        {
            Assert.Equal("application/x-pem-file", pemResponse.Content.Headers.ContentType?.MediaType);
        }

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, pem, KeyBlobFormat.PkixPublicKeyText);
        Assert.Equal(keyId, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(publicKey.Export(KeyBlobFormat.RawPublicKey))));
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, statement, signature));
        await AssertOpenSslVerifiesAsync(pem, statement, signature);
    }

    [Fact]
    public async Task Unknown_parts_and_keys_are_an_empty_404()
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync();
        var revisionPath = _s.Revision(capabilityId, revision.GetProperty("id").GetString()!);
        foreach (var path in new[] { revisionPath + "/document", revisionPath + "/attestation", revisionPath + "/native/x", $"{RevisionClient.Keys(_s.OrgA, _s.PublisherId)}/{Guid.NewGuid()}/public-key", $"{RevisionClient.Keys(_s.OrgA, _s.PublisherId)}/not-a-key/public-key" })
        {
            using var response = await _s.Erin.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>A second, unrelated verifier: OpenSSL 3's raw Ed25519 verify. LibreSSL lacks <c>-rawin</c>, so it is skipped there.</summary>
    private static async Task AssertOpenSslVerifiesAsync(byte[] pem, byte[] statement, byte[] signature)
    {
        var dir = Directory.CreateTempSubdirectory("goz-0007-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "key.pem"), pem, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "statement.json"), statement, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "statement.sig"), signature, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("openssl", ["pkeyutl", "-verify", "-pubin", "-inkey", "key.pem", "-rawin", "-in", "statement.json", "-sigfile", "statement.sig"])
            {
                WorkingDirectory = dir.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // no openssl on this machine
            }

            using (process)
            {
                await process!.WaitForExitAsync(TestContext.Current.CancellationToken);
                var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) + await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                if (output.Contains("rawin", StringComparison.Ordinal) && process.ExitCode != 0)
                {
                    return; // LibreSSL: option unknown
                }

                Assert.True(process.ExitCode == 0, output);
                Assert.Contains("Signature Verified Successfully", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
