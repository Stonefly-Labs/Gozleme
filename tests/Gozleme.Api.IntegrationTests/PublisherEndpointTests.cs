using System.Net;
using System.Text.RegularExpressions;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;
using Gozleme.Api.Publishers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0005 through the API (contracts/publishers-api.openapi.yaml): US1 register, create and rename with
/// byte-identical ids (SC-001); US2 the one "unavailable" answer (SC-004), conflicts, no identity change and
/// concurrency (SC-007); US3 the role × action matrix (SC-005), identical not-found answers (SC-006) and role loss
/// (FR-021); US5 blocked, earmarked and suspended publishers; US4 the namespace rule; and the registration rate limit
/// (FR-008, maintainer decision U1).
/// </summary>
public sealed partial class PublisherEndpointTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Unavailable = """{"error":"handle_unavailable"}""";

    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!; // A: admin, member
    private HttpClient _carol = null!; // A: member, publisher
    private HttpClient _erin = null!; // A: member
    private HttpClient _bob = null!; // B: admin, member
    private Guid _carolId;
    private string _orgA = null!;
    private string _orgB = null!;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        // A clock with sub-microsecond ticks on every platform (Linux's resolution), so the byte-identical
        // response/read assertions also cover PostgreSQL's microsecond timestamps.
        _factory = new GozlemeApiFactory(Db.AppConnectionString, configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new SubMicrosecondTimeProvider())));
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_erin, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = await OrgClient.CreateAsync(_alice, "Publishers A");
        _orgB = await OrgClient.CreateAsync(_bob, "Publishers B");
        (_carol, _carolId) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(_orgA));
        await OrgClient.JoinAsync(_alice, _erin, _orgA);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _alice, _carol, _erin, _bob })
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Register_create_and_rename_keep_every_identifier_byte_identical()
    {
        var handle = OrgClient.NewHandle("acme");
        using var registered = await OrgClient.RegisterPublisherRawAsync(_alice, _orgA, handle, "Acme Tools");
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var publisher = await AuthClient.JsonAsync(registered);
        var publisherId = publisher.GetProperty("id").GetString()!;
        Assert.Equal(handle, publisher.GetProperty("handle").GetString());
        Assert.Equal(_orgA, publisher.GetProperty("organizationId").GetString()); // FR-009: the opaque organization reference
        Assert.Equal($"gz://{GozlemeDatabase.Origin}/pub/{publisherId}", publisher.GetProperty("canonicalId").GetString());
        Assert.False(publisher.TryGetProperty("publisherId", out _)); // one name per meaning (maintainer, 2026-11-17)
        Assert.Matches(UuidV7(), publisherId);
        Assert.Equal("active", publisher.GetProperty("state").GetString());
        Assert.Equal($"/api/v1/organizations/{_orgA}/publishers/{publisherId}", registered.Headers.Location?.OriginalString);

        // Reads are member-level.
        var listed = await OrgClient.GetJsonAsync(_erin, OrgClient.Publishers(_orgA));
        Assert.Equal(GozlemeDatabase.Origin, listed.GetProperty("origin").GetString());
        Assert.Contains(listed.GetProperty("publishers").EnumerateArray(), p => p.GetProperty("id").GetString() == publisherId);
        Assert.Equal(publisher.GetRawText(), (await OrgClient.GetJsonAsync(_erin, OrgClient.Publisher(_orgA, publisherId))).GetRawText());

        var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "weather-lookup", "Weather Lookup");
        var capabilityId = capability.GetProperty("id").GetString()!;
        var canonical = capability.GetProperty("canonicalId").GetString();
        Assert.Equal($"gz://{GozlemeDatabase.Origin}/cap/{capabilityId}", canonical);
        Assert.Equal($"{handle}/weather-lookup", capability.GetProperty("alias").GetString());
        Assert.Equal(publisherId, capability.GetProperty("publisherId").GetString());

        foreach (var name in new[] { "Weather Lookup v2", "Forecasts" })
        {
            using var renamed = await OrgClient.RenameCapabilityAsync(_carol, _orgA, publisherId, capabilityId, new { displayName = name });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            var json = await AuthClient.JsonAsync(renamed);
            Assert.Equal(name, json.GetProperty("displayName").GetString());
            Assert.Equal(canonical, json.GetProperty("canonicalId").GetString());
            Assert.Equal($"{handle}/weather-lookup", json.GetProperty("alias").GetString());
            Assert.Equal("weather-lookup", json.GetProperty("name").GetString());
        }

        var read = await OrgClient.GetJsonAsync(_erin, OrgClient.Capability(_orgA, publisherId, capabilityId));
        Assert.Equal(canonical, read.GetProperty("canonicalId").GetString());
        Assert.Equal("Forecasts", read.GetProperty("displayName").GetString());
        Assert.Single((await OrgClient.GetJsonAsync(_erin, OrgClient.Capabilities(_orgA, publisherId))).GetProperty("capabilities").EnumerateArray());

        // The same value again succeeds and writes no second audit event (spec Edge Cases).
        var audits = AuditCount($"Capability renamed {capabilityId}");
        using (var same = await OrgClient.RenameCapabilityAsync(_carol, _orgA, publisherId, capabilityId, new { displayName = "Forecasts" }))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        }

        Assert.Equal(2, audits);
        Assert.Equal(audits, AuditCount($"Capability renamed {capabilityId}"));

        using (var publisherRenamed = await OrgClient.RenamePublisherAsync(_alice, _orgA, publisherId, new { displayName = "Acme Tools Inc" }))
        {
            Assert.Equal(HttpStatusCode.OK, publisherRenamed.StatusCode);
            var json = await AuthClient.JsonAsync(publisherRenamed);
            Assert.Equal(publisher.GetProperty("canonicalId").GetString(), json.GetProperty("canonicalId").GetString());
            Assert.Equal(handle, json.GetProperty("handle").GetString());
        }

        foreach (var bad in new[] { string.Empty, "   ", new string('x', 101) })
        {
            using var invalid = await OrgClient.RenameCapabilityAsync(_carol, _orgA, publisherId, capabilityId, new { displayName = bad });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("validation_failed", (await AuthClient.JsonAsync(invalid)).GetProperty("error").GetString());
            using var invalidPublisher = await OrgClient.RenamePublisherAsync(_alice, _orgA, publisherId, new { displayName = bad });
            Assert.Equal(HttpStatusCode.BadRequest, invalidPublisher.StatusCode);
        }
    }

    [Fact]
    public async Task Registration_is_rate_limited_per_account_only_after_authorization()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Publishers:RegisterPerHour"] = "2" });
        var (admin, _) = await AuthClient.SignedInAsync(factory);
        var (other, _) = await AuthClient.SignedInAsync(factory);
        var (stranger, _) = await AuthClient.SignedInAsync(factory);
        var (plain, _) = await AuthClient.SignedInAsync(factory);
        using var _a = admin;
        using var _o = other;
        using var _s = stranger;
        using var _p = plain;
        var org = await OrgClient.CreateAsync(admin, "Rate limited");
        await OrgClient.JoinAsync(admin, other, org);
        await OrgClient.SetRolesAsync(admin, org, await OrgClient.UserIdAsync(other), "admin");
        await OrgClient.JoinAsync(admin, plain, org);

        // A non-member and a plain member never get 429, however often they try, and spend nobody's budget.
        for (var i = 0; i < 4; i++)
        {
            using var notMember = await OrgClient.RegisterPublisherRawAsync(stranger, org, OrgClient.NewHandle());
            Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
            using var forbidden = await OrgClient.RegisterPublisherRawAsync(plain, org, OrgClient.NewHandle());
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        await OrgClient.RegisterPublisherAsync(admin, org);
        using (var invalid = await OrgClient.RegisterPublisherRawAsync(admin, org, "Not Valid"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); // validation runs after the limiter: it spends one
        }

        var logged = factory.Logs.Count;
        using (var limited = await OrgClient.RegisterPublisherRawAsync(admin, org, OrgClient.NewHandle()))
        {
            Assert.Equal((HttpStatusCode)429, limited.StatusCode);
            Assert.Equal("""{"error":"rate_limited"}""", await limited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(limited.Headers.RetryAfter);
            Assert.True(limited.Headers.CacheControl?.NoStore);
        }

        var after = factory.Logs.Skip(logged).ToList();
        Assert.Contains(after, l => l.Contains($"Publisher registration refused (rate_limited) in {org}", StringComparison.Ordinal));
        Assert.DoesNotContain(after, l => l.Contains("Sign-in failed", StringComparison.Ordinal) || l.Contains("Registration refused", StringComparison.Ordinal));

        // Another admin's budget is its own.
        await OrgClient.RegisterPublisherAsync(other, org);
    }

    [Fact]
    public async Task A_taken_blocked_or_foreign_earmarked_handle_gets_one_identical_answer()
    {
        var taken = OrgClient.NewHandle("taken");
        var blocked = OrgClient.NewHandle("blocked");
        var earmarkedForB = OrgClient.NewHandle("earmark");
        var earmarkedForA = OrgClient.NewHandle("mine");
        await OrgClient.RegisterPublisherAsync(_alice, _orgA, taken);
        await GozlemeDatabase.ExecuteAsync(
            $"""
            INSERT INTO gozleme.handle_claims (id, handle, status) VALUES (gen_random_uuid(), '{blocked}', 'blocked');
            INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES (gen_random_uuid(), '{earmarkedForB}', 'earmarked', '{_orgB}');
            INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES (gen_random_uuid(), '{earmarkedForA}', 'earmarked', '{_orgA}');
            """,
            Db.MigratorConnectionString);

        var answers = new List<(HttpStatusCode Status, string Body, bool NoStore)>
        {
            await OrgClient.SnapshotAsync(_bob, HttpMethod.Post, OrgClient.Publishers(_orgB), new { handle = taken, displayName = "B" }), // another organization
            await OrgClient.SnapshotAsync(_alice, HttpMethod.Post, OrgClient.Publishers(_orgA), new { handle = taken, displayName = "A" }), // the holder's own retry
            await OrgClient.SnapshotAsync(_alice, HttpMethod.Post, OrgClient.Publishers(_orgA), new { handle = blocked, displayName = "A" }),
            await OrgClient.SnapshotAsync(_alice, HttpMethod.Post, OrgClient.Publishers(_orgA), new { handle = earmarkedForB, displayName = "A" }),
        };
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.Conflict, Unavailable, true), a));

        // The earmarked organization's admin consumes its earmark; then it is taken like any other.
        var mine = await OrgClient.RegisterPublisherAsync(_alice, _orgA, earmarkedForA);
        Assert.Equal("held", await Db.ScalarAsync<string>($"SELECT status FROM gozleme.handle_claims WHERE handle = '{earmarkedForA}' AND publisher_id = '{mine.GetProperty("id").GetString()}'"));
        Assert.Equal((HttpStatusCode.Conflict, Unavailable, true), await OrgClient.SnapshotAsync(_bob, HttpMethod.Post, OrgClient.Publishers(_orgB), new { handle = earmarkedForA, displayName = "B" }));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.publishers WHERE organization_id = '{_orgB}'"));
    }

    [Fact]
    public async Task Duplicates_conflict_and_no_route_changes_a_handle_name_or_organization()
    {
        var handle = OrgClient.NewHandle();
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA, handle)).GetProperty("id").GetString()!;
        var capabilityId = (await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "lookup")).GetProperty("id").GetString()!;
        Assert.Equal(
            (HttpStatusCode.Conflict, """{"error":"conflict"}""", true),
            await OrgClient.SnapshotAsync(_carol, HttpMethod.Post, OrgClient.Capabilities(_orgA, publisherId), new { name = "lookup", displayName = "Again" }));

        using (var publisher = await OrgClient.RenamePublisherAsync(_alice, _orgA, publisherId, new { displayName = "Renamed", handle = "other-handle", organizationId = _orgB, id = Guid.NewGuid() }))
        {
            var json = await AuthClient.JsonAsync(publisher);
            Assert.Equal((handle, _orgA, publisherId), (json.GetProperty("handle").GetString(), json.GetProperty("organizationId").GetString(), json.GetProperty("id").GetString()));
        }

        using (var capability = await OrgClient.RenameCapabilityAsync(_carol, _orgA, publisherId, capabilityId, new { displayName = "Renamed", name = "other", publisherId = Guid.NewGuid(), alias = "x/y" }))
        {
            var json = await AuthClient.JsonAsync(capability);
            Assert.Equal(("lookup", $"{handle}/lookup", publisherId), (json.GetProperty("name").GetString(), json.GetProperty("alias").GetString(), json.GetProperty("publisherId").GetString()));
        }

        Assert.Equal(handle, await Db.ScalarAsync<string>($"SELECT handle FROM gozleme.publishers WHERE id = '{publisherId}'"));
        Assert.Equal(_orgA, (await Db.ScalarAsync<Guid>($"SELECT organization_id FROM gozleme.publishers WHERE id = '{publisherId}'")).ToString());
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await OrgClient.SnapshotAsync(_alice, method, OrgClient.Publisher(_orgA, publisherId), new { handle = "x" })).Status);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await OrgClient.SnapshotAsync(_carol, method, OrgClient.Capability(_orgA, publisherId, capabilityId), new { name = "x" })).Status);
        }
    }

    [Fact]
    public async Task Concurrent_registrations_and_creations_have_exactly_one_winner_and_never_a_500()
    {
        var (carol2, _) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(_orgA));
        using var _c = carol2;
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        for (var round = 0; round < 10; round++)
        {
            var handle = OrgClient.NewHandle("race");
            var registrations = await Task.WhenAll(
                OrgClient.SnapshotAsync(_alice, HttpMethod.Post, OrgClient.Publishers(_orgA), new { handle, displayName = "A" }),
                OrgClient.SnapshotAsync(_bob, HttpMethod.Post, OrgClient.Publishers(_orgB), new { handle, displayName = "B" }));
            Assert.Single(registrations, r => r.Status == HttpStatusCode.Created);
            Assert.Single(registrations, r => r.Status == HttpStatusCode.Conflict && r.Body == Unavailable);
            Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.handle_claims WHERE handle = '{handle}'"));
            Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.publishers WHERE handle = '{handle}'"));

            var name = $"race-{round}";
            var creations = await Task.WhenAll(
                OrgClient.SnapshotAsync(_carol, HttpMethod.Post, OrgClient.Capabilities(_orgA, publisherId), new { name, displayName = "One" }),
                OrgClient.SnapshotAsync(carol2, HttpMethod.Post, OrgClient.Capabilities(_orgA, publisherId), new { name, displayName = "Two" }));
            Assert.Single(creations, r => r.Status == HttpStatusCode.Created);
            Assert.Single(creations, r => r.Status == HttpStatusCode.Conflict && r.Body == """{"error":"conflict"}""");
        }
    }

    [Fact]
    public async Task Every_role_and_action_matches_the_contract_table()
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        var capabilityId = (await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "matrix")).GetProperty("id").GetString()!;
        foreach (var role in OrganizationRole.All)
        {
            var (client, _) = await AuthClient.SignedInAsync(_factory);
            using var _ = client;
            await OrgClient.JoinAsync(_alice, client, _orgA);
            await OrgClient.SetRolesAsync(_alice, _orgA, await OrgClient.UserIdAsync(client), role == OrganizationRole.Member ? [] : [role]);
            string[] roles = role == OrganizationRole.Member ? [role] : [role, OrganizationRole.Member];
            foreach (var (action, resource) in OrgAction.All.Where(a => a.Resource is OrgResource.Publisher or OrgResource.Capability))
            {
                var (method, path, body, success) = Call(action, publisherId, capabilityId, role);
                var answer = await OrgClient.SnapshotAsync(client, method, path, body);
                if (OrganizationPermissions.Allows(roles, action, resource))
                {
                    Assert.True(answer.Status == success, $"{role} {action}: {answer.Status} {answer.Body}");
                }
                else
                {
                    Assert.True(answer is (HttpStatusCode.Forbidden, """{"error":"forbidden"}""", true), $"{role} {action}: {answer.Status} {answer.Body}");
                }
            }
        }
    }

    [Fact]
    public async Task Non_members_and_foreign_or_malformed_ids_get_the_identical_empty_404()
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        var capabilityId = (await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "secret")).GetProperty("id").GetString()!;
        var otherPublisher = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        var foreignPublisher = (await OrgClient.RegisterPublisherAsync(_bob, _orgB)).GetProperty("id").GetString()!;
        var unknown = Guid.NewGuid().ToString();

        // Holds every action, so only the target decides (permission is checked before the target).
        var (both, bothId) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(_orgA));
        using var _b = both;
        await OrgClient.SetRolesAsync(_alice, _orgA, bothId.ToString(), "admin", "publisher");

        // Organization level: a non-member with real ids, an unknown and a malformed organization, on all eight endpoints.
        foreach (var (client, org) in new[] { (_bob, _orgA), (_alice, unknown), (_alice, "not-a-guid") })
        {
            foreach (var (method, path, body) in Targets(org, publisherId, capabilityId))
            {
                await AssertEmpty404(client, method, path, body);
            }
        }

        // Publisher level: organization B's publisher under A's path, an unknown and a malformed id.
        foreach (var publisher in new[] { foreignPublisher, unknown, "not-a-guid" })
        {
            foreach (var (method, path, body) in Targets(_orgA, publisher, capabilityId).Skip(2))
            {
                await AssertEmpty404(both, method, path, body);
            }
        }

        // Capability level: under the wrong publisher, unknown and malformed.
        foreach (var (publisher, capability) in new[] { (otherPublisher, capabilityId), (publisherId, unknown), (publisherId, "not-a-guid") })
        {
            foreach (var (method, path, body) in Targets(_orgA, publisher, capability).Skip(6))
            {
                await AssertEmpty404(both, method, path, body);
            }
        }
    }

    private static async Task AssertEmpty404(HttpClient client, HttpMethod method, string path, object? body)
    {
        var answer = await OrgClient.SnapshotAsync(client, method, path, body);
        Assert.True(answer is (HttpStatusCode.NotFound, "", true), $"{method} {path}: {answer.Status} {answer.Body}");
    }

    [Fact]
    public async Task Losing_roles_or_leaving_changes_nothing_and_account_deletion_only_clears_the_creator()
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "kept");
        var capabilityId = capability.GetProperty("id").GetString()!;
        await OrgClient.SetRolesAsync(_alice, _orgA, _carolId.ToString());
        Assert.Equal(capability.GetRawText(), (await OrgClient.GetJsonAsync(_alice, OrgClient.Capability(_orgA, publisherId, capabilityId))).GetRawText());
        using (var removed = await OrgClient.RemoveAsync(_alice, _orgA, _carolId.ToString()))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(capability.GetRawText(), (await OrgClient.GetJsonAsync(_alice, OrgClient.Capability(_orgA, publisherId, capabilityId))).GetRawText());
        Assert.Contains((await OrgClient.GetJsonAsync(_erin, OrgClient.Capabilities(_orgA, publisherId))).GetProperty("capabilities").EnumerateArray(), c => c.GetProperty("id").GetString() == capabilityId);

        // Deleting the creating account nulls created_by and nothing else (the FK cascade).
        var before = await Row(capabilityId);
        await GozlemeDatabase.ExecuteAsync($"DELETE FROM gozleme.users WHERE id = '{_carolId}'", Db.SuperuserConnectionString);
        Assert.Equal(before.Replace(_carolId.ToString(), string.Empty, StringComparison.Ordinal), await Row(capabilityId));
        Assert.Equal(capability.GetProperty("alias").GetString(), (await OrgClient.GetJsonAsync(_alice, OrgClient.Capability(_orgA, publisherId, capabilityId))).GetProperty("alias").GetString());

        async Task<string> Row(string id) =>
            await Db.ScalarAsync<string>($"SELECT concat_ws('|', id, organization_id, publisher_id, name, display_name, created_at, coalesce(created_by::text, ''), updated_at) FROM gozleme.capabilities WHERE id = '{id}'");
    }

    [Fact]
    public async Task A_reclaimed_publisher_is_suspended_with_unchanged_identifiers()
    {
        var handle = OrgClient.NewHandle("squat");
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA, handle)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, "forecast");
        var capabilityId = capability.GetProperty("id").GetString()!;
        await GozlemeDatabase.ExecuteAsync(
            $"UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE publisher_id = '{publisherId}'", Db.MigratorConnectionString);

        var publisher = await OrgClient.GetJsonAsync(_erin, OrgClient.Publisher(_orgA, publisherId));
        Assert.Equal(("suspended", handle), (publisher.GetProperty("state").GetString(), publisher.GetProperty("handle").GetString()));
        var read = await OrgClient.GetJsonAsync(_erin, OrgClient.Capability(_orgA, publisherId, capabilityId));
        Assert.Equal("suspended", read.GetProperty("state").GetString());
        Assert.Equal(
            (capability.GetProperty("canonicalId").GetString(), capability.GetProperty("alias").GetString()),
            (read.GetProperty("canonicalId").GetString(), read.GetProperty("alias").GetString()));

        const string suspended = """{"error":"suspended"}""";
        Assert.Equal((HttpStatusCode.Conflict, suspended, true), await OrgClient.SnapshotAsync(_carol, HttpMethod.Post, OrgClient.Capabilities(_orgA, publisherId), new { name = "radar", displayName = "R" }));
        Assert.Equal((HttpStatusCode.Conflict, suspended, true), await OrgClient.SnapshotAsync(_alice, HttpMethod.Patch, OrgClient.Publisher(_orgA, publisherId), new { displayName = "R" }));
        Assert.Equal((HttpStatusCode.Conflict, suspended, true), await OrgClient.SnapshotAsync(_carol, HttpMethod.Patch, OrgClient.Capability(_orgA, publisherId, capabilityId), new { displayName = "R" }));

        // The handle is free again; the retired alias stays reserved to its original capability forever (FR-035).
        var successor = (await OrgClient.RegisterPublisherAsync(_bob, _orgB, handle)).GetProperty("id").GetString()!;
        var (bobPublisher, _) = await TwoTenants.AddPublisherMemberAsync(_factory, _bob, Guid.Parse(_orgB));
        using var _p = bobPublisher;
        Assert.Equal((HttpStatusCode.Conflict, """{"error":"conflict"}""", true), await OrgClient.SnapshotAsync(bobPublisher, HttpMethod.Post, OrgClient.Capabilities(_orgB, successor), new { name = "forecast", displayName = "F" }));
        Assert.Equal($"{handle}/radar", (await OrgClient.CreateCapabilityAsync(bobPublisher, _orgB, successor, "radar")).GetProperty("alias").GetString());
    }

    /// <summary>Valid as handles and as names; the test database is fresh per run, so the exact values can be registered.</summary>
    public static TheoryData<string> ValidValues => ["ab", "a1", "acme-tools", "a-1-b-2", "x" + new string('9', 38)];

    public static TheoryData<string> InvalidHandles =>
    [
        "a", "Acme", "acme_tools", "acme.tools", "-acme", "acme-", "acme--tools", "1acme", "acme tools", " acme", "acme\t", "acİme", "acıme",
        "аcme", "ａcme", new string('a', 40), "admin", "gozleme", "www", "",
    ];

    [Theory]
    [MemberData(nameof(ValidValues))]
    public async Task Valid_handles_and_names_are_stored_exactly_as_typed(string value)
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _orgA, value)).GetProperty("id").GetString()!;
        Assert.Equal(value, await Db.ScalarAsync<string>($"SELECT handle FROM gozleme.publishers WHERE id = '{publisherId}'"));
        foreach (var name in new[] { value, value + new string('z', NamespaceRules.MaxCapabilityNameLength - value.Length) })
        {
            var capability = await OrgClient.CreateCapabilityAsync(_carol, _orgA, publisherId, name);
            Assert.Equal(name, capability.GetProperty("name").GetString());
            Assert.Equal($"{value}/{name}", capability.GetProperty("alias").GetString());
            Assert.Equal(name, await Db.ScalarAsync<string>($"SELECT name FROM gozleme.capabilities WHERE id = '{capability.GetProperty("id").GetString()}'"));
        }
    }

    [Theory]
    [MemberData(nameof(InvalidHandles))]
    public async Task Invalid_handles_and_names_are_refused_with_the_rule_and_create_nothing(string value)
    {
        var before = await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.publishers WHERE organization_id = '{_orgA}'");
        using (var refused = await OrgClient.RegisterPublisherRawAsync(_alice, _orgA, value))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var json = await AuthClient.JsonAsync(refused);
            Assert.Equal("validation_failed", json.GetProperty("error").GetString());
            Assert.Equal(NamespaceRules.RuleText.Handle, json.GetProperty("fields").GetProperty("handle")[0].GetString());
        }

        Assert.Equal(before, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.publishers WHERE organization_id = '{_orgA}'"));
        if (value is "admin" or "gozleme" or "www")
        {
            return; // reserved handles are valid capability names
        }

        var publisherId = await PublisherAsync();
        using var name = await OrgClient.CreateCapabilityRawAsync(_carol, _orgA, publisherId, value == new string('a', 40) ? new string('a', 65) : value);
        Assert.Equal(HttpStatusCode.BadRequest, name.StatusCode);
        Assert.Equal(NamespaceRules.RuleText.CapabilityName, (await AuthClient.JsonAsync(name)).GetProperty("fields").GetProperty("name")[0].GetString());
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.capabilities WHERE publisher_id = '{publisherId}'"));
    }

    [Fact]
    public async Task The_database_checks_refuse_invalid_handles_and_names_for_both_logins()
    {
        var publisherId = Guid.Parse(await PublisherAsync());
        var aliceId = Guid.Parse(await OrgClient.UserIdAsync(_alice));
        var asAlice = GozlemeDatabase.Establish(GozlemeDatabase.SignContext(aliceId));
        var asCarol = GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_carolId));
        foreach (var bad in new[] { "Acme", "acİme", "admin", "a", "acme--x" })
        {
            var publisher = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(
                Db.AppConnectionString, asAlice,
                $"INSERT INTO gozleme.publishers (id, organization_id, handle, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), '{_orgA}', $1, 'X', now(), '{aliceId}', now())",
                bad));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, publisher.SqlState);
            var claim = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Db.AsOwnerAsync<object>("INSERT INTO gozleme.handle_claims (id, handle, status) VALUES (gen_random_uuid(), $1, 'blocked')", bad));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, claim.SqlState);
        }

        foreach (var bad in new[] { "Lookup", "look_up", "-x", "x" })
        {
            var capability = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(
                Db.AppConnectionString, asCarol,
                $"INSERT INTO gozleme.capabilities (id, organization_id, publisher_id, name, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), '{_orgA}', '{publisherId}', $1, 'X', now(), '{_carolId}', now())",
                bad));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, capability.SqlState);
        }
    }

    private async Task<string> PublisherAsync() => (await OrgClient.RegisterPublisherAsync(_alice, _orgA)).GetProperty("id").GetString()!;

    private int AuditCount(string text) => _factory.Logs.Count(l => l.Contains("Gozleme.Security.Audit", StringComparison.Ordinal) && l.Contains(text, StringComparison.Ordinal));

    private (HttpMethod Method, string Path, object? Body, HttpStatusCode Success) Call(string action, string publisherId, string capabilityId, string role) => action switch
    {
        OrgAction.PublisherList => (HttpMethod.Get, OrgClient.Publishers(_orgA), null, HttpStatusCode.OK),
        OrgAction.PublisherRead => (HttpMethod.Get, OrgClient.Publisher(_orgA, publisherId), null, HttpStatusCode.OK),
        OrgAction.PublisherRegister => (HttpMethod.Post, OrgClient.Publishers(_orgA), new { handle = OrgClient.NewHandle(), displayName = role }, HttpStatusCode.Created),
        OrgAction.PublisherRename => (HttpMethod.Patch, OrgClient.Publisher(_orgA, publisherId), new { displayName = role }, HttpStatusCode.OK),
        OrgAction.CapabilityList => (HttpMethod.Get, OrgClient.Capabilities(_orgA, publisherId), null, HttpStatusCode.OK),
        OrgAction.CapabilityRead => (HttpMethod.Get, OrgClient.Capability(_orgA, publisherId, capabilityId), null, HttpStatusCode.OK),
        OrgAction.CapabilityCreate => (HttpMethod.Post, OrgClient.Capabilities(_orgA, publisherId), new { name = $"by-{role}", displayName = role }, HttpStatusCode.Created),
        OrgAction.CapabilityRename => (HttpMethod.Patch, OrgClient.Capability(_orgA, publisherId, capabilityId), new { displayName = role }, HttpStatusCode.OK),
        OrgAction.ManifestValidate => (HttpMethod.Post, ManifestClient.Validate(_orgA, publisherId, capabilityId), new { }, HttpStatusCode.OK), // GOZ-0006
        _ => throw new InvalidOperationException(action),
    };

    private static IEnumerable<(HttpMethod Method, string Path, object? Body)> Targets(string org, string publisher, string capability) =>
    [
        (HttpMethod.Get, OrgClient.Publishers(org), null),
        (HttpMethod.Post, OrgClient.Publishers(org), new { handle = OrgClient.NewHandle(), displayName = "X" }),
        (HttpMethod.Get, OrgClient.Publisher(org, publisher), null),
        (HttpMethod.Patch, OrgClient.Publisher(org, publisher), new { displayName = "X" }),
        (HttpMethod.Get, OrgClient.Capabilities(org, publisher), null),
        (HttpMethod.Post, OrgClient.Capabilities(org, publisher), new { name = "probe", displayName = "X" }),
        (HttpMethod.Get, OrgClient.Capability(org, publisher, capability), null),
        (HttpMethod.Patch, OrgClient.Capability(org, publisher, capability), new { displayName = "X" }),
        (HttpMethod.Post, ManifestClient.Validate(org, publisher, capability), new { }), // GOZ-0006
    ];

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV7();

    private sealed class SubMicrosecondTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            var now = DateTimeOffset.UtcNow;
            return now.AddTicks(7 - (now.Ticks % TimeSpan.TicksPerMicrosecond));
        }
    }
}
