using System.Diagnostics;
using Gozleme.Api.IntegrationTests.Infrastructure;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0004 FR-019–FR-021: every authorization decision emits a bounded span and counter; denials and
/// role changes are audited with opaque ids only; no personal data reaches spans, metric labels or logs.
/// </summary>
public sealed class RbacTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] AuthzLabelKeys = ["gozleme.authz.action", "gozleme.authz.resource_type", "gozleme.authz.outcome"];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Decisions_role_changes_and_last_admin_refusals_are_observable_without_personal_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, aliceEmail) = await AuthClient.SignedInAsync(_factory);
        var (carol, carolEmail) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _c = carol;
        const string name = "Authz Secret Name";
        var org = await OrgClient.CreateAsync(alice, name);
        var (_, code) = await OrgClient.InviteAsync(alice, org);
        using (var redeemed = await OrgClient.RedeemAsync(carol, code))
        {
            Assert.True(redeemed.IsSuccessStatusCode);
        }

        var (aliceId, carolId) = (await OrgClient.UserIdAsync(alice), await OrgClient.UserIdAsync(carol));
        (await OrgClient.RenameAsync(carol, org, "Denied")).Dispose(); // deny
        await OrgClient.SetRolesAsync(alice, org, carolId, "admin"); // allow + 1302
        (await OrgClient.RenameAsync(carol, org, "Allowed")).Dispose(); // allow
        await OrgClient.SetRolesAsync(carol, org, aliceId);
        (await OrgClient.SetRolesRawAsync(carol, org, carolId)).Dispose(); // last_admin

        var spans = await WaitForAsync(
            () => _factory.SpansSnapshot(),
            s => s.Any(a => a.DisplayName == "gozleme.org.member_roles" && (string?)a.GetTagItem("gozleme.org.failure_category") == "last_admin"),
            ct);
        var decisions = spans.Where(s => s.DisplayName == "gozleme.authz.decision").ToList();
        Assert.Contains(decisions, s => Tag(s, "gozleme.authz.action") == "organization.rename" && Tag(s, "gozleme.authz.outcome") == "deny" && Tag(s, "gozleme.authz.resource_type") == "organization");
        Assert.Contains(decisions, s => Tag(s, "gozleme.authz.action") == "organization.rename" && Tag(s, "gozleme.authz.outcome") == "allow");
        Assert.All(decisions, s => Assert.All(s.TagObjects, t => Assert.Contains(t.Key, AuthzLabelKeys)));
        Assert.Contains(spans, s => s.DisplayName == "gozleme.org.rename" && Tag(s, "gozleme.org.failure_category") == "forbidden");

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var authzLabels = Labels(metrics, "gozleme.authz.decisions");
        Assert.Contains("gozleme.authz.outcome=deny", authzLabels);
        Assert.All(authzLabels, l => Assert.Contains(l.Split('=')[0], AuthzLabelKeys));
        var orgLabels = Labels(metrics, "gozleme.org.operations");
        Assert.Contains("gozleme.org.failure_category=forbidden", orgLabels);
        Assert.Contains("gozleme.org.failure_category=last_admin", orgLabels);

        var logs = string.Join('\n', _factory.Logs);
        Assert.Contains($"Gozleme.Security.Audit: Authorization denied (organization.rename) in {org} {carolId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Member {carolId} roles changed from member to admin,member in {org} by {aliceId}", logs, StringComparison.Ordinal);

        // Spans are collected process-wide; keep the API's request traces (other tests' direct SQL carries literal contexts).
        var requestTraces = spans.Where(s => s.Kind == ActivityKind.Server).Select(s => s.TraceId).ToHashSet();
        Assert.NotEmpty(requestTraces);
        var exported = string.Join('\n', spans.Where(s => requestTraces.Contains(s.TraceId)).SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)));
        foreach (var value in new[] { aliceEmail, carolEmail, name, code, code.Replace("-", string.Empty, StringComparison.Ordinal), "v1.", "gozleme_session=", GozlemeDatabase.ContextKey })
        {
            Assert.DoesNotContain(value, exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, logs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(authzLabels.Concat(orgLabels), l => l.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(decisions, s => s.TagObjects.Any(t => t.Value?.ToString() is { } v && (v == aliceId || v == carolId || v.Contains("admin", StringComparison.Ordinal))));
    }

    private static string? Tag(Activity span, string key) => (string?)span.GetTagItem(key);

    private static List<string> Labels(Metric[] metrics, string name)
    {
        var labels = new List<string>();
        foreach (var metric in metrics.Where(m => m.Name == name))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    labels.Add($"{tag.Key}={tag.Value}");
                }
            }
        }

        return labels;
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
