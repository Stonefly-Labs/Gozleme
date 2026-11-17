using System.Diagnostics;
using System.Net;
using Gozleme.Api.Identity;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Publishers;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 US5 operator handle commands (contracts/operator-commands.md): exit codes and output, reclaim with
/// earmark in one transaction, opaque ids only (no handle in output, span or audit), and no HTTP route for any of it.
/// </summary>
public sealed class OperatorHandleCommandTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private Guid _orgA;
    private Guid _orgB;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = Guid.Parse(await OrgClient.CreateAsync(_alice, "Operator A"));
        _orgB = Guid.Parse(await OrgClient.CreateAsync(_bob, "Operator B"));
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _bob.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Reserve_and_release_block_and_free_a_handle()
    {
        var handle = OrgClient.NewHandle("res");
        var (code, stdout, _) = await RunAsync("handle-reserve", "--handle", handle);
        Assert.Equal(HandleOperatorCommands.Ok, code);
        Assert.Matches("^Reserved handle claim [0-9a-f-]{36} \\(blocked\\)$", stdout.Trim());
        Assert.Equal("blocked", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{handle}'"));
        Assert.Equal(HandleOperatorCommands.NotFree, (await RunAsync("handle-reserve", "--handle", handle)).Code);
        using (var refused = await OrgClient.RegisterPublisherRawAsync(_alice, _orgA.ToString(), handle))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        (code, stdout, _) = await RunAsync("handle-release", "--handle", handle);
        Assert.Equal(HandleOperatorCommands.Ok, code);
        Assert.Matches("^Released handle claim [0-9a-f-]{36}$", stdout.Trim());
        var (again, _, stderr) = await RunAsync("handle-release", "--handle", handle);
        Assert.Equal((HandleOperatorCommands.UnknownTarget, "No unused reservation for that handle."), (again, stderr.Trim()));
        await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), handle);
        Assert.Equal(HandleOperatorCommands.UnknownTarget, (await RunAsync("handle-release", "--handle", handle)).Code); // held, not a reservation
    }

    [Fact]
    public async Task Reclaim_suspends_the_holder_and_can_earmark_in_the_same_transaction()
    {
        var handle = OrgClient.NewHandle("rec");
        var publisher = (await OrgClient.RegisterPublisherAsync(_bob, _orgB.ToString(), handle)).GetProperty("id").GetString()!;
        var spans = new List<Activity>();
        using var listener = Listen(spans);

        var (code, stdout, _) = await RunAsync("handle-reclaim", "--handle", handle, "--organization", _orgA.ToString());
        Assert.Equal(HandleOperatorCommands.Ok, code);
        var lines = stdout.Trim().Split('\n');
        Assert.Matches($"^Reclaimed handle claim [0-9a-f-]{{36}}; publisher {publisher} of organization {_orgB} suspended$", lines[0].Trim());
        Assert.Matches($"^Reserved handle claim [0-9a-f-]{{36}} \\(earmarked for organization {_orgA}\\)$", lines[1].Trim());
        Assert.Equal("suspended", (await OrgClient.GetJsonAsync(_bob, OrgClient.Publisher(_orgB.ToString(), publisher))).GetProperty("state").GetString());
        Assert.Equal("earmarked", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{handle}' AND status IN ('blocked','earmarked','held')"));

        Assert.Equal(HandleOperatorCommands.UnknownTarget, (await RunAsync("handle-reclaim", "--handle", handle)).Code); // nothing held now
        await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString(), handle);

        var span = Assert.Single(spans, s => s.OperationName == "gozleme.operator.handle_reclaim" && (int?)s.GetTagItem(PublisherTelemetry.ExitCodeTag) == 0);
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains(handle, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(handle, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_earmark_during_reclaim_rolls_the_reclaim_back()
    {
        var handle = OrgClient.NewHandle("rb");
        var publisher = (await OrgClient.RegisterPublisherAsync(_bob, _orgB.ToString(), handle)).GetProperty("id").GetString()!;
        var (code, _, stderr) = await RunAsync("handle-reclaim", "--handle", handle, "--organization", Guid.NewGuid().ToString());
        Assert.Equal((HandleOperatorCommands.UnknownTarget, "No such organization."), (code, stderr.Trim()));
        Assert.Equal("active", (await OrgClient.GetJsonAsync(_bob, OrgClient.Publisher(_orgB.ToString(), publisher))).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("handle-reserve", new[] { "--handle", "admin" }, HandleOperatorCommands.InvalidHandle)]
    [InlineData("handle-reserve", new[] { "--handle", "Acme" }, HandleOperatorCommands.InvalidHandle)]
    [InlineData("handle-reclaim", new[] { "--handle", "a" }, HandleOperatorCommands.InvalidHandle)]
    [InlineData("handle-reserve", new string[0], HandleOperatorCommands.Usage)]
    [InlineData("handle-reserve", new[] { "--handle" }, HandleOperatorCommands.Usage)]
    [InlineData("handle-reserve", new[] { "--handle", "ok-handle", "--organization", "not-a-guid" }, HandleOperatorCommands.Usage)]
    [InlineData("handle-release", new[] { "--handle", "ok-handle", "--organization", "0193a000-0000-7000-8000-000000000002" }, HandleOperatorCommands.Usage)]
    [InlineData("handle-reclaim", new[] { "--handle", "ok-handle", "--unknown", "x" }, HandleOperatorCommands.Usage)]
    public async Task Invalid_input_answers_its_exit_code(string command, string[] args, int expected)
    {
        var (code, _, stderr) = await RunAsync(command, args);
        Assert.Equal(expected, code);
        Assert.Equal(
            expected == HandleOperatorCommands.Usage ? "Usage: " : $"Invalid handle: {NamespaceRules.RuleText.Handle}",
            expected == HandleOperatorCommands.Usage ? stderr.Trim()[..7] : stderr.Trim());
    }

    [Fact]
    public async Task An_earmark_for_an_unknown_organization_is_a_missing_target()
    {
        var (code, _, stderr) = await RunAsync("handle-reserve", "--handle", OrgClient.NewHandle(), "--organization", Guid.NewGuid().ToString());
        Assert.Equal((HandleOperatorCommands.UnknownTarget, "No such organization."), (code, stderr.Trim()));
    }

    [Fact]
    public async Task An_unreachable_database_fails_with_the_class_name_only()
    {
        var (code, _, stderr) = await RunAsync(
            "handle-reserve", ["--handle", OrgClient.NewHandle()], "Host=127.0.0.1;Port=1;Database=x;Username=gozleme_migrator;Password=secret-Xy;Timeout=2");
        Assert.Equal(HandleOperatorCommands.Failed, code);
        Assert.Matches("^Operator command failed \\([A-Za-z]+\\)\\.$", stderr.Trim());
        Assert.DoesNotContain("secret-Xy", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/handles")]
    [InlineData("/api/v1/reclaim")]
    [InlineData("/api/v1/reservations")]
    [InlineData("/api/v1/unsuspend")]
    [InlineData("/api/v1/transfer")]
    [InlineData("/api/v1/organizations/{org}/publishers/{pub}/reclaim")]
    [InlineData("/api/v1/organizations/{org}/publishers/{pub}/unsuspend")]
    [InlineData("/api/v1/organizations/{org}/publishers/{pub}/transfer")]
    public async Task No_http_route_reaches_the_operator_surface(string template)
    {
        var publisher = (await OrgClient.RegisterPublisherAsync(_alice, _orgA.ToString())).GetProperty("id").GetString()!;
        var path = template.Replace("{org}", _orgA.ToString(), StringComparison.Ordinal).Replace("{pub}", publisher, StringComparison.Ordinal);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var (status, _, _) = await OrgClient.SnapshotAsync(_alice, method, path, new { handle = "x" });
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path}: {status}");
        }
    }

    private Task<(int Code, string Stdout, string Stderr)> RunAsync(string command, params string[] args) => RunAsync(command, args, Db.MigratorConnectionString);

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string command, string[] args, string connectionString)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await HandleOperatorCommands.RunAsync(command, [.. args, $"--ConnectionStrings:GozlemeMigrator={connectionString}"], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GozlemeTelemetry.ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
