using System.Net;
using System.Text;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T052 (US3-AS3–AS5, FR-014, FR-015): a capability rename and a GOZ-0005 <c>handle-reclaim</c> change no
/// earlier revision. The statement names the same canonical IDs with no alias, handle or display name, still verifies,
/// and the stored projection is returned rather than re-derived; after the reclaim the revisions carry
/// <c>publisherSuspended: true</c> and publication is refused <c>409 suspended</c>.
/// </summary>
public sealed class RevisionProvenanceTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task Rename_and_reclaim_leave_earlier_revisions_unchanged_and_verified()
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, first) = await _s.PublishedAsync("1.0.0", publisherId);
        var path = _s.Revision(capabilityId, first.GetProperty("id").GetString()!, publisherId);
        var (_, before) = await RevisionClient.GetAsync(_s.Erin, path);
        var statementBefore = await _s.Erin.GetByteArrayAsync(new Uri(path + "/statement", UriKind.Relative), TestContext.Current.CancellationToken);

        using (var renamed = await OrgClient.RenameCapabilityAsync(_s.Carol, _s.OrgA, publisherId, capabilityId, new { displayName = "Renamed Display Name" }))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }

        var (_, afterRename) = await RevisionClient.GetAsync(_s.Erin, path);
        Assert.Equal(before.GetRawText(), afterRename.GetRawText());

        await _s.ReclaimAsync(handle);
        var (status, afterReclaim) = await RevisionClient.GetAsync(_s.Erin, path);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(afterReclaim.GetProperty("publisherSuspended").GetBoolean());
        Assert.Equal("verified", afterReclaim.GetProperty("verification").GetProperty("status").GetString());
        foreach (var member in new[] { "capabilityId", "publisherId", "nativeDigest", "projection", "projectionDigest", "attestation", "publishedAt", "sequence" })
        {
            Assert.Equal(before.GetProperty(member).GetRawText(), afterReclaim.GetProperty(member).GetRawText());
        }

        Assert.Equal(canonical, afterReclaim.GetProperty("attestation").GetProperty("statement").GetProperty("capability").GetString());
        Assert.Equal(statementBefore, await _s.Erin.GetByteArrayAsync(new Uri(path + "/statement", UriKind.Relative), TestContext.Current.CancellationToken));
        var text = Encoding.UTF8.GetString(statementBefore);
        foreach (var name in new[] { handle, "Renamed Display Name", "IT Capability", "IT Publisher", "/cap-" })
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }

        var (_, list) = await RevisionClient.GetAsync(_s.Erin, _s.Revisions(capabilityId, publisherId));
        Assert.True(list.GetProperty("publisherSuspended").GetBoolean());
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"suspended"}"""),
            await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId, publisherId), RevisionClient.Envelope(canonical, "2.0.0")));
    }

    [Fact]
    public async Task The_stored_projection_is_returned_not_re_derived()
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync();
        var id = revision.GetProperty("id").GetString()!;

        // A projection that no adapter would derive, written with the guard triggers bypassed, is returned as stored
        // (and fails its digest), which shows reads never run the adapter again.
        await GozlemeDatabase.ExecuteAsync($$"""
            SET session_replication_role = replica;
            UPDATE gozleme.manifest_revisions SET projection = convert_to('{"projectionVersion":"1","stored":true}', 'UTF8') WHERE id = '{{id}}';
            """, _s.Db.SuperuserConnectionString);
        var (_, read) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, id));
        Assert.True(read.GetProperty("projection").GetProperty("stored").GetBoolean());
        Assert.Equal("projection_digest_mismatch", read.GetProperty("verification").GetProperty("reason").GetString());
    }
}
