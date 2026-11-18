using System.Diagnostics;
using System.Net;
using System.Text;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Revisions;
using Gozleme.Api.Telemetry;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T068 (FR-021, SC-005, research R16): <c>gozleme.manifest.publish</c> and <c>gozleme.manifest.verify</c>
/// spans and the counters <c>gozleme.manifest.publications</c> and <c>gozleme.manifest.verifications</c> with closed
/// label sets; the operator succession span with its exit code only; and no KEK, private or encrypted key bytes,
/// statement, signature (base64 or hex), digest, version, document, capability name or alias in spans, metric labels
/// or captured logs.
/// </summary>
public sealed class RevisionTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Version = "7.3.1-telemetry.42";

    private static readonly string[] PublishTags =
        ["gozleme.manifest.family", "gozleme.manifest.outcome", "gozleme.organization.id", "gozleme.publisher.id", "gozleme.capability.id", "gozleme.revision.id"];

    private static readonly string[] VerifyTags = ["gozleme.revision.id", "gozleme.manifest.outcome", "gozleme.manifest.verification_reason"];

    private static readonly string[] Families = ["mcp", "a2a", "api", "event", "skill", "model", "unknown"];

    private static readonly string[] Outcomes =
        ["published", "rejected", "version_exists", "forbidden", "not_found", "suspended", "rate_limited", "too_large", "unsupported_media_type", "unavailable", "verified", "failed"];

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task Publications_and_verifications_are_observable_with_bounded_labels_and_no_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var capability = await OrgClient.GetJsonAsync(_s.Erin, OrgClient.Capability(_s.OrgA, _s.PublisherId, capabilityId));
        var (name, alias) = (capability.GetProperty("name").GetString()!, capability.GetProperty("alias").GetString()!);
        var path = _s.Revisions(capabilityId);
        var revision = await RevisionClient.PublishedAsync(_s.Carol, path, RevisionClient.Envelope(canonical, Version));
        var revisionId = revision.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await ManifestClient.SendAsync(_s.Carol, path, RevisionClient.Envelope(canonical, Version))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ManifestClient.SendAsync(_s.Carol, path, "{not json"u8.ToArray())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(_s.Alice, path, "{}"u8.ToArray())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ManifestClient.SendAsync(_s.Bob, path, "{}"u8.ToArray())).Status);

        // A tampered revision so a failed verification with its reason is counted.
        var (tamperedCapability, _, tampered) = await _s.PublishedAsync("1.0.0-telemetry-tamper");
        await GozlemeDatabase.ExecuteAsync(
            $"SET session_replication_role = replica; UPDATE gozleme.revision_attestations SET signature = set_byte(signature, 0, get_byte(signature, 0) # 1) WHERE revision_id = '{tampered.GetProperty("id").GetString()}';",
            _s.Db.SuperuserConnectionString);
        Assert.Equal(HttpStatusCode.OK, (await RevisionClient.GetAsync(_s.Erin, _s.Revision(tamperedCapability, tampered.GetProperty("id").GetString()!))).Status);
        Assert.Equal(HttpStatusCode.OK, (await RevisionClient.GetAsync(_s.Erin, path)).Status);
        var statement = await _s.Erin.GetByteArrayAsync(new Uri(_s.Revision(capabilityId, revisionId) + "/statement", UriKind.Relative), ct);
        var signature = await _s.Erin.GetByteArrayAsync(new Uri(_s.Revision(capabilityId, revisionId) + "/signature", UriKind.Relative), ct);

        // The operator span: its exit code only. The listener is process-wide and other classes run succession in
        // parallel, so only spans in this test's own trace count.
        var operatorSpans = new List<Activity>();
        using (var parent = new Activity("revision-telemetry-test").Start())
        using (Listen(operatorSpans, parent.TraceId))
        {
            Assert.Equal(0, (await _s.SuccessionAsync(null, null, "--publisher", _s.PublisherId, "--reason", "lost")).Code);
        }

        var succession = Assert.Single(operatorSpans, s => s.DisplayName.StartsWith("gozleme.operator.signing", StringComparison.Ordinal));
        Assert.Equal(["gozleme.operator.exit_code"], succession.TagObjects.Select(t => t.Key));

        var spans = _s.Factory.SpansSnapshot();
        var publishes = spans.Where(s => s.DisplayName == RevisionTelemetry.PublishSpanName).ToList();
        Assert.True(publishes.Count >= 6, $"{publishes.Count} publish spans");
        Assert.All(publishes, s => Assert.All(s.TagObjects, t => Assert.Contains(t.Key, PublishTags)));
        Assert.All(publishes, s => Assert.Contains(Tag(s, "gozleme.manifest.family"), Families));
        Assert.All(publishes, s => Assert.Contains(Tag(s, "gozleme.manifest.outcome"), Outcomes));
        var published = Assert.Single(publishes, s => Tag(s, "gozleme.revision.id") == revisionId);
        Assert.Equal(("published", "mcp", capabilityId), (Tag(published, "gozleme.manifest.outcome"), Tag(published, "gozleme.manifest.family"), Tag(published, "gozleme.capability.id")));
        Assert.Contains(publishes, s => Tag(s, "gozleme.manifest.outcome") == "version_exists" && s.Status == ActivityStatusCode.Error);
        Assert.Contains(publishes, s => Tag(s, "gozleme.manifest.outcome") == "rejected" && s.Status != ActivityStatusCode.Error);
        Assert.Contains(publishes, s => Tag(s, "gozleme.manifest.outcome") == "forbidden" && Tag(s, "gozleme.organization.id") == _s.OrgA && Tag(s, "gozleme.capability.id") is null);
        Assert.Null(Tag(Assert.Single(publishes, s => Tag(s, "gozleme.manifest.outcome") == "not_found"), "gozleme.organization.id"));

        var verifies = spans.Where(s => s.DisplayName == RevisionTelemetry.VerifySpanName).ToList();
        Assert.All(verifies, s => Assert.All(s.TagObjects, t => Assert.Contains(t.Key, VerifyTags)));
        Assert.Contains(verifies, s => Tag(s, "gozleme.manifest.outcome") == "verified" && Tag(s, "gozleme.manifest.verification_reason") == "none");
        Assert.Contains(verifies, s => Tag(s, "gozleme.manifest.outcome") == "failed" && Tag(s, "gozleme.manifest.verification_reason") == "signature_invalid");

        _s.Factory.FlushMetrics();
        Metric[] metrics;
        lock (_s.Factory.Metrics)
        {
            metrics = [.. _s.Factory.Metrics];
        }

        var publications = Points(metrics, "gozleme.manifest.publications");
        Assert.Contains("gozleme.manifest.family=mcp;gozleme.manifest.outcome=published", publications);
        Assert.Contains("gozleme.manifest.family=mcp;gozleme.manifest.outcome=version_exists", publications);
        Assert.Contains("gozleme.manifest.family=unknown;gozleme.manifest.outcome=forbidden", publications);
        var verifications = Points(metrics, "gozleme.manifest.verifications");
        Assert.Contains("gozleme.manifest.outcome=failed;gozleme.manifest.verification_reason=signature_invalid", verifications);
        Assert.Contains("gozleme.manifest.outcome=verified;gozleme.manifest.verification_reason=none", verifications);
        var reasons = VerificationReason.All.Append(RevisionTelemetry.NoReason).ToList();
        foreach (var label in publications.Concat(verifications).SelectMany(p => p.Split(';')))
        {
            var (key, value) = (label.Split('=')[0], label.Split('=')[1]);
            Assert.Contains(value, key switch { "gozleme.manifest.family" => Families, "gozleme.manifest.outcome" => Outcomes, _ => reasons });
        }

        var encrypted = await _s.Db.ScalarAsync<byte[]>($"SELECT encrypted_private_key FROM gozleme.publisher_signing_keys WHERE publisher_id = '{_s.PublisherId}' ORDER BY generation LIMIT 1");
        var exported = string.Join('\n', spans.Concat(operatorSpans)
            .SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
                .Concat(s.Events.Select(e => e.Name + string.Join(',', e.Tags.Select(t => $"{t.Key}={t.Value}"))))));
        var logs = string.Join('\n', _s.Factory.Logs);
        var nativeDigest = revision.GetProperty("nativeDigest").GetString()!;
        string[] forbidden =
        [
            RevisionClient.TestKek, Convert.ToBase64String(encrypted), Convert.ToHexString(encrypted), Convert.ToHexStringLower(encrypted),
            Convert.ToBase64String(statement), Encoding.UTF8.GetString(statement), Convert.ToBase64String(signature), Convert.ToHexStringLower(signature),
            Convert.ToHexString(signature), nativeDigest, nativeDigest["sha256:".Length..], revision.GetProperty("projectionDigest").GetString()!["sha256:".Length..],
            Version, name, alias, revision.GetProperty("attestation").GetProperty("keyId").GetString()!["sha256:".Length..],
        ];
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(publications.Concat(verifications), p => p.Contains(value, StringComparison.Ordinal));
        }
    }

    private static string? Tag(Activity span, string key) => span.GetTagItem(key)?.ToString();

    private static List<string> Points(Metric[] metrics, string name)
    {
        var points = new List<string>();
        foreach (var metric in metrics.Where(m => m.Name == name))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new List<string>();
                foreach (var tag in point.Tags)
                {
                    tags.Add($"{tag.Key}={tag.Value}");
                }

                points.Add(string.Join(';', tags.Order(StringComparer.Ordinal)));
            }
        }

        return points;
    }

    private static ActivityListener Listen(List<Activity> spans, ActivityTraceId trace)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GozlemeTelemetry.ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId == trace && a.DisplayName.StartsWith("gozleme.operator.", StringComparison.Ordinal))
                {
                    lock (spans)
                    {
                        spans.Add(a);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
