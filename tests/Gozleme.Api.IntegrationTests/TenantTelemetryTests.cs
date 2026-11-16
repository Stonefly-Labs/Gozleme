using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Gozleme.Api.Identity;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 US5 and the transaction filter (FR-023, FR-026–FR-029, SC-006): the tenant span and its
/// events, the bounded denial counter, audit events 1201–1203, refused writes answered as "not found",
/// and no token, key, code, hash, email or display name in any span, metric label or log.
/// </summary>
public sealed class TenantTelemetryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Reasons = ["missing", "invalid", "expired", "refused_write"];

    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Organization_requests_run_in_one_tenant_span_that_establishes_and_clears_the_context_without_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alice, aliceEmail) = await AuthClient.SignedInAsync(_factory);
        var (bob, bobEmail) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        using var _b = bob;
        const string name = "Tenant Telemetry Secret Name";
        var org = await OrgClient.CreateAsync(alice, name);
        (await OrgClient.RenameAsync(alice, org, name + " 2")).Dispose();
        var (_, code) = await OrgClient.InviteAsync(alice, org);
        (await OrgClient.RedeemAsync(bob, code)).Dispose();

        var spans = await WaitForAsync(() => _factory.SpansSnapshot(), s => s.Any(a => a.DisplayName == "gozleme.org.invite_redeem"), ct);
        var rename = spans.Single(s => s.DisplayName == "gozleme.org.rename");
        var tenant = spans.Single(s => s.DisplayName == TenantTelemetry.TransactionSpan && s.SpanId == rename.ParentSpanId);
        Assert.Equal(["gozleme.tenant.context.established", "gozleme.tenant.context.cleared"], tenant.Events.Select(e => e.Name));
        Assert.Equal("ok", tenant.Events.First().Tags.Single(t => t.Key == TenantTelemetry.ResultTag).Value);
        Assert.Equal("commit", tenant.Events.Last().Tags.Single(t => t.Key == TenantTelemetry.OutcomeTag).Value);
        Assert.All(spans.Where(s => s.DisplayName == TenantTelemetry.TransactionSpan), s => Assert.Null(s.GetTagItem(OrganizationTelemetry.OrganizationIdTag)));
        Assert.Equal(org, (string?)rename.GetTagItem(OrganizationTelemetry.OrganizationIdTag));

        var hash = Convert.ToHexStringLower(InvitationCodes.Hash(InvitationCodes.Normalize(code)!));
        string[] forbidden = [aliceEmail, bobEmail, name, code, hash, "v1.", GozlemeDatabase.ContextKey];
        var exported = Exported(spans);
        var logs = string.Join('\n', _factory.Logs);
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, exported, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_key_that_differs_from_the_provisioned_one_fails_closed_and_is_counted_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, _) = await AuthClient.SignedInAsync(_factory);
        using var _o = owner;
        var org = await OrgClient.CreateAsync(owner, "Key mismatch");

        await using var mismatched = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Rls:ContextKey"] = "a-different-key-of-sufficient-length-02" });
        using var client = AuthClient.Create(mismatched);
        var email = AuthClient.NewEmail();
        (await AuthClient.RegisterAsync(client, email, AuthClient.Password)).Dispose();
        using var list = await AuthClient.GetAsync(client, OrgClient.Base);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await AuthClient.JsonAsync(list)).GetProperty("organizations").EnumerateArray());
        using var create = await OrgClient.CreateRawAsync(client, new { displayName = "Never created" });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode); // the insert is refused by RLS and answered as not found
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.organizations WHERE id = '{org}'"));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM gozleme.organizations WHERE display_name = 'Never created'"));

        var spans = await WaitForAsync(() => mismatched.SpansSnapshot(), s => s.Count(a => a.DisplayName == TenantTelemetry.TransactionSpan) >= 2, ct);
        Assert.Contains(spans, s => s.DisplayName == TenantTelemetry.TransactionSpan && s.Status == ActivityStatusCode.Error
            && s.Events.Any(e => e.Tags.Any(t => t.Key == TenantTelemetry.ResultTag && (string?)t.Value == "invalid")));

        var userId = await mismatched.Services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<GozlemeUser>>().FindByEmailAsync(email);
        var logs = await WaitForAsync(() => string.Join('\n', mismatched.Logs), l => l.Contains("self-check failed", StringComparison.Ordinal), ct);
        Assert.Contains($"Gozleme.Security.Audit: Tenant context rejected (invalid) {userId!.Id}", logs, StringComparison.Ordinal);
        Assert.Contains("Gozleme.Security.Audit: Row-level security refused a write", logs, StringComparison.Ordinal);
        Assert.Contains("Gozleme.Security.Audit: Tenant context self-check failed (invalid)", logs, StringComparison.Ordinal);

        var labels = DenialLabels(mismatched);
        Assert.Contains("gozleme.tenant.denial.reason=invalid", labels);
        Assert.Contains("gozleme.tenant.denial.reason=refused_write", labels);
        Assert.All(labels, l => Assert.Contains(l.Split('=')[1], Reasons));
    }

    [Fact]
    public async Task A_refused_write_ends_its_organization_span_with_a_bounded_failure_and_counts_it()
    {
        // The refusal unwinds through the handler, so the organization span must be closed as a failure
        // before the filter turns the refusal into "not found" (GOZ-0002 R11 bounded span contract).
        var ct = TestContext.Current.CancellationToken;
        await using var mismatched = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Rls:ContextKey"] = "a-different-key-of-sufficient-length-03" });
        using var client = AuthClient.Create(mismatched);
        (await AuthClient.RegisterAsync(client, AuthClient.NewEmail(), AuthClient.Password)).Dispose();
        using var create = await OrgClient.CreateRawAsync(client, new { displayName = "Refused create" });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        var spans = await WaitForAsync(() => mismatched.SpansSnapshot(), s => s.Any(a => a.DisplayName == "gozleme.org.create"), ct);
        var span = Assert.Single(spans, s => s.DisplayName == "gozleme.org.create");
        Assert.Equal("failure", (string?)span.GetTagItem(OrganizationTelemetry.OutcomeTag));
        Assert.Equal(OrgFailure.RefusedWrite, (string?)span.GetTagItem(OrganizationTelemetry.CategoryTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.GetTagItem(OrganizationTelemetry.OrganizationIdTag));
        Assert.Contains($"{OrganizationTelemetry.CategoryTag}={OrgFailure.RefusedWrite}", Labels(mismatched, "gozleme.org.operations"));
    }

    [Fact]
    public async Task A_refused_cross_tenant_write_is_answered_as_not_found_and_counted()
    {
        var t = await TwoTenants.CreateAsync(_factory);
        using var _t = t;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GozlemeDbContext>();
        var filter = new TenantTransaction(db, scope.ServiceProvider.GetRequiredService<TenantContextSigner>(), scope.ServiceProvider.GetRequiredService<SecurityAudit>());
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, t.AliceId.ToString())], "test")) };

        // A handler with a defect: it skips the membership check and writes into tenant B.
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), async _ =>
        {
            db.Invitations.Add(new Invitation
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = t.OrgB,
                CodeHash = InvitationCodes.Hash(InvitationCodes.Normalize(InvitationCodes.Generate())!),
                CreatedBy = t.AliceId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
            return TypedResults.NoContent();
        });

        Assert.IsType<NotFound>(result);
        Assert.Equal(1, await TwoTenants.TruthAsync(Db, "invitations", t.OrgB));
        Assert.Contains("gozleme.tenant.denial.reason=refused_write", DenialLabels(_factory));
        Assert.Contains($"Gozleme.Security.Audit: Row-level security refused a write {t.AliceId}", string.Join('\n', _factory.Logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Organization_endpoints_without_a_session_principal_establish_nothing()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GozlemeDbContext>();
        var filter = new TenantTransaction(db, scope.ServiceProvider.GetRequiredService<TenantContextSigner>(), scope.ServiceProvider.GetRequiredService<SecurityAudit>());
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        long? seen = null;
        await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), async _ =>
        {
            seen = await db.Organizations.LongCountAsync();
            return TypedResults.Ok();
        });
        Assert.Equal(0, seen);
        Assert.Contains("gozleme.tenant.denial.reason=missing", DenialLabels(_factory));
    }

    private static List<string> DenialLabels(GozlemeApiFactory factory) => Labels(factory, "gozleme.tenant.denials");

    private static List<string> Labels(GozlemeApiFactory factory, string metricName)
    {
        factory.FlushMetrics();
        Metric[] metrics;
        lock (factory.Metrics)
        {
            metrics = [.. factory.Metrics];
        }

        var labels = new List<string>();
        foreach (var metric in metrics.Where(m => m.Name == metricName))
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

    /// <summary>
    /// Spans of API requests only: Npgsql's activity source is process-wide, so commands issued by other
    /// tests' helpers (which put literal tokens in SQL text) would otherwise appear here as root spans.
    /// </summary>
    private static string Exported(IReadOnlyCollection<Activity> spans)
    {
        var requests = spans.Where(s => s.Kind == ActivityKind.Server).Select(s => s.TraceId).ToHashSet();
        return string.Join('\n', spans.Where(s => requests.Contains(s.TraceId)).SelectMany(s =>
            s.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(s.DisplayName).Append(s.StatusDescription ?? string.Empty)
                .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))));
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
