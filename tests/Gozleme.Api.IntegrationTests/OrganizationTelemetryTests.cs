using System.Diagnostics;
using Gozleme.Api.IntegrationTests.Infrastructure;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// FR-023, FR-026, SC-006: every organization operation emits a bounded span and counter; emails,
/// display names, invitation codes and session values never reach spans, metric labels or logs;
/// audit events carry opaque ids only.
/// </summary>
public sealed class OrganizationTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Operations =
        ["create", "rename", "select", "invite_create", "invite_revoke", "invite_redeem", "member_remove", "member_leave"];

    private static readonly string[] Categories = ["none", "validation", "not_found", "invalid_invitation", "last_member", "rate_limited", "refused_write"];

    private static readonly string[] LabelKeys = ["gozleme.org.operation", "gozleme.org.outcome", "gozleme.org.failure_category"];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Organization_operations_emit_bounded_spans_counters_and_audit_events_without_personal_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, aliceEmail) = await AuthClient.SignedInAsync(_factory);
        var (bob, bobEmail) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        const string name = "Telemetry Secret Name Ç";
        const string renamed = "Renamed Secret Name";

        var org = await OrgClient.CreateAsync(alice, name);
        (await OrgClient.RenameAsync(alice, org, renamed)).Dispose();
        (await OrgClient.CreateRawAsync(alice, new { displayName = "" })).Dispose(); // validation failure
        (await OrgClient.SelectAsync(alice, org)).Dispose();
        var (revokedId, revokedCode) = await OrgClient.InviteAsync(alice, org);
        (await OrgClient.RevokeAsync(alice, org, revokedId)).Dispose();
        (await OrgClient.RedeemAsync(bob, revokedCode)).Dispose(); // invalid_invitation
        var (invitationId, code) = await OrgClient.InviteAsync(alice, org);
        (await OrgClient.RedeemAsync(bob, code)).Dispose();
        var aliceId = await OrgClient.UserIdAsync(alice);
        var bobId = await OrgClient.UserIdAsync(bob);
        (await OrgClient.RemoveAsync(alice, org, bobId)).Dispose();
        (await OrgClient.SelectAsync(bob, org)).Dispose(); // not_found after removal
        await OrgClient.JoinAsync(alice, bob, org);
        (await OrgClient.RemoveAsync(bob, org, bobId)).Dispose(); // leave
        (await OrgClient.RemoveAsync(alice, org, aliceId)).Dispose(); // last_member

        var spans = await WaitForAsync(() => _factory.SpansSnapshot(), s => Operations.All(op => s.Any(a => a.DisplayName == $"gozleme.org.{op}")), ct);
        var orgSpans = spans.Where(s => s.DisplayName.StartsWith("gozleme.org.", StringComparison.Ordinal)).ToList();
        foreach (var op in Operations)
        {
            Assert.Contains(orgSpans, s => s.DisplayName == $"gozleme.org.{op}" && (string?)s.GetTagItem("gozleme.org.outcome") == "success");
        }

        Assert.All(orgSpans, s => Assert.Contains((string?)s.GetTagItem("gozleme.org.failure_category"), Categories));
        Assert.Contains(orgSpans, s => s.DisplayName == "gozleme.org.invite_redeem" && s.Status == ActivityStatusCode.Error && (string?)s.GetTagItem("gozleme.org.failure_category") == "invalid_invitation");
        Assert.Contains(orgSpans, s => s.DisplayName == "gozleme.org.member_leave" && (string?)s.GetTagItem("gozleme.org.failure_category") == "last_member");
        Assert.Contains(orgSpans, s => s.DisplayName == "gozleme.org.select" && (string?)s.GetTagItem("gozleme.org.failure_category") == "not_found" && s.GetTagItem("gozleme.organization.id") is null);
        Assert.Contains(orgSpans, s => s.DisplayName == "gozleme.org.create" && (string?)s.GetTagItem("gozleme.organization.id") == org);

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var labels = new List<string>();
        foreach (var metric in metrics.Where(m => m.Name == "gozleme.org.operations"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    labels.Add($"{tag.Key}={tag.Value}");
                }
            }
        }

        Assert.Contains("gozleme.org.operation=invite_redeem", labels);
        Assert.All(labels, l => Assert.Contains(l.Split('=')[0], LabelKeys));

        var logs = string.Join('\n', _factory.Logs);
        Assert.Contains($"Gozleme.Security.Audit: Organization created {org} by {aliceId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Invitation redeemed {invitationId}; {bobId} joined {org}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Member {bobId} removed from {org} by {aliceId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Member {bobId} left {org}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Invitation redemption refused (invalid_invitation) {bobId}", logs, StringComparison.Ordinal);

        var exported = string.Join('\n', spans.SelectMany(s =>
            s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
                .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))));
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal);
        string[] forbidden = [aliceEmail, bobEmail, name, renamed, code, normalized, revokedCode, "gozleme_session="];
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, logs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(labels, l => l.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(orgSpans, s => s.TagObjects.Any(t => t.Value?.ToString() == aliceId || t.Value?.ToString() == bobId));
    }

    private static async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = probe();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(100, ct);
        }
    }
}
