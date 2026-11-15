using System.Diagnostics;
using Gozleme.Api.IntegrationTests.Infrastructure;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// FR-029–FR-031, SC-006: auth outcome spans and bounded counters exist; emails, passwords, cookies
/// and session keys never reach spans, metrics or logs; audit events carry the opaque id only.
/// </summary>
public sealed class IdentityTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Operations = ["gozleme.auth.register", "gozleme.auth.sign_in", "gozleme.auth.sign_out", "gozleme.auth.sign_out_all"];
    private static readonly string[] Categories = ["none", "invalid_credentials", "locked_out", "validation", "duplicate", "registration_closed", "rate_limited", "forbidden", "unauthenticated"];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Auth_flows_emit_bounded_spans_counters_and_audit_events_without_personal_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = AuthClient.NewEmail("telemetry");
        const string password = "telemetry-secret-passphrase";
        using var client = AuthClient.Create(_factory);
        using var registered = await AuthClient.RegisterAsync(client, email, password);
        var userId = (await AuthClient.JsonAsync(registered)).GetProperty("userId").GetString()!;
        var cookie = AuthClient.SessionCookiePair(registered);
        (await AuthClient.SignInAsync(client, email, "wrong-" + password)).Dispose();
        using var signedIn = await AuthClient.SignInAsync(client, email, password);
        var cookie2 = AuthClient.SessionCookiePair(signedIn);
        (await AuthClient.PostAsync(client, "/api/v1/auth/sign-out")).Dispose();
        (await AuthClient.SignInAsync(client, email, password)).Dispose();
        (await AuthClient.PostAsync(client, "/api/v1/auth/sign-out-all")).Dispose();

        var spans = await WaitForAsync(() => _factory.SpansSnapshot(), s => Operations.All(op => s.Any(a => a.DisplayName == op)), ct);
        foreach (var op in Operations)
        {
            Assert.Contains(spans, s => s.DisplayName == op && (string?)s.GetTagItem("gozleme.auth.outcome") == "success");
        }

        Assert.Contains(spans, s => s.DisplayName == "gozleme.auth.sign_in" && s.Status == ActivityStatusCode.Error
            && (string?)s.GetTagItem("gozleme.auth.failure_category") == "invalid_credentials");
        Assert.All(spans.Where(s => s.DisplayName.StartsWith("gozleme.auth.", StringComparison.Ordinal)), s =>
            Assert.Contains((string?)s.GetTagItem("gozleme.auth.failure_category"), Categories));

        _factory.FlushMetrics();
        Metric[] metrics;
        lock (_factory.Metrics)
        {
            metrics = [.. _factory.Metrics];
        }

        var labels = new List<string>();
        foreach (var metric in metrics)
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    labels.Add($"{metric.Name}|{tag.Key}={tag.Value}");
                }
            }
        }

        Assert.Contains("gozleme.auth.attempts|gozleme.auth.operation=sign_in", labels);
        Assert.Contains("gozleme.auth.attempts|gozleme.auth.failure_category=invalid_credentials", labels);
        Assert.All(labels.Where(l => l.StartsWith("gozleme.auth.attempts|gozleme.auth.failure_category=", StringComparison.Ordinal)), l =>
            Assert.Contains(l.Split('=')[1], Categories));

        var logs = string.Join('\n', _factory.Logs);
        Assert.Contains($"Gozleme.Security.Audit: Account registered {userId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Sign-in failed (invalid_credentials) {userId}", logs, StringComparison.Ordinal);
        Assert.Contains($"Gozleme.Security.Audit: Signed out everywhere {userId}", logs, StringComparison.Ordinal);

        var exported = string.Join('\n', spans.SelectMany(s =>
            s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
                .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))));
        string[] forbidden = [email, email.ToUpperInvariant(), password, cookie.Split('=', 2)[1], cookie2.Split('=', 2)[1], "gozleme_session"];
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.Ordinal);
            Assert.DoesNotContain(value, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(labels, l => l.Contains(value, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(spans, s => s.DisplayName.StartsWith("gozleme.auth.", StringComparison.Ordinal) && s.TagObjects.Any(t => t.Value?.ToString() == userId));
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
