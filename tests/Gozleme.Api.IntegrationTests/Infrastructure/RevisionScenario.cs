using System.Collections.Concurrent;
using System.Text.Json;
using Gozleme.Api.Revisions;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// GOZ-0007 suites' common tenant: in organization A alice (admin, member), carol (member, publisher) and erin
/// (member), one publisher with a known handle; in organization B bob (admin) and bobPublisher with their own
/// publisher and capability. Each test makes its own capabilities so suites never share revision rows.
/// </summary>
public sealed class RevisionScenario : IAsyncDisposable
{
    private RevisionScenario(GozlemeDatabase db, GozlemeApiFactory factory) => (Db, Factory) = (db, factory);

    public GozlemeDatabase Db { get; }

    public GozlemeApiFactory Factory { get; }

    public HttpClient Alice { get; private set; } = null!;

    public HttpClient Carol { get; private set; } = null!;

    public Guid CarolId { get; private set; }

    public HttpClient Erin { get; private set; } = null!;

    public HttpClient Bob { get; private set; } = null!;

    public HttpClient BobPublisher { get; private set; } = null!;

    public string OrgA { get; private set; } = null!;

    public string OrgB { get; private set; } = null!;

    public string PublisherId { get; private set; } = null!;

    public string Handle { get; private set; } = null!;

    public string ForeignPublisherId { get; private set; } = null!;

    public string ForeignCapabilityId { get; private set; } = null!;

    public static async Task<RevisionScenario> CreateAsync(GozlemeDatabase db, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var merged = new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "10000" };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            merged[key] = value;
        }

        var s = new RevisionScenario(db, new GozlemeApiFactory(db.AppConnectionString, settings: merged));
        (s.Alice, _) = await AuthClient.SignedInAsync(s.Factory);
        (s.Erin, _) = await AuthClient.SignedInAsync(s.Factory);
        (s.Bob, _) = await AuthClient.SignedInAsync(s.Factory);
        s.OrgA = await OrgClient.CreateAsync(s.Alice, "Revisions A");
        s.OrgB = await OrgClient.CreateAsync(s.Bob, "Revisions B");
        (s.Carol, s.CarolId) = await TwoTenants.AddPublisherMemberAsync(s.Factory, s.Alice, Guid.Parse(s.OrgA));
        (s.BobPublisher, _) = await TwoTenants.AddPublisherMemberAsync(s.Factory, s.Bob, Guid.Parse(s.OrgB));
        await OrgClient.JoinAsync(s.Alice, s.Erin, s.OrgA);
        (s.PublisherId, s.Handle) = await s.NewPublisherAsync();
        s.ForeignPublisherId = (await OrgClient.RegisterPublisherAsync(s.Bob, s.OrgB)).GetProperty("id").GetString()!;
        s.ForeignCapabilityId = (await OrgClient.CreateCapabilityAsync(s.BobPublisher, s.OrgB, s.ForeignPublisherId, "manifest-demo")).GetProperty("id").GetString()!;
        return s;
    }

    public async Task<(string Id, string Handle)> NewPublisherAsync()
    {
        var handle = OrgClient.NewHandle("rv");
        return ((await OrgClient.RegisterPublisherAsync(Alice, OrgA, handle)).GetProperty("id").GetString()!, handle);
    }

    public async Task<(string Id, string Canonical)> NewCapabilityAsync(string? publisherId = null)
    {
        var capability = await OrgClient.CreateCapabilityAsync(Carol, OrgA, publisherId ?? PublisherId, $"cap-{Guid.NewGuid():N}"[..20]);
        return (capability.GetProperty("id").GetString()!, capability.GetProperty("canonicalId").GetString()!);
    }

    public string Revisions(string capabilityId, string? publisherId = null) => RevisionClient.Revisions(OrgA, publisherId ?? PublisherId, capabilityId);

    public string Revision(string capabilityId, string revisionId, string? publisherId = null) => RevisionClient.Revision(OrgA, publisherId ?? PublisherId, capabilityId, revisionId);

    /// <summary>A new capability with one published revision (carol); returns the detail.</summary>
    public async Task<(string CapabilityId, string Canonical, JsonElement Revision)> PublishedAsync(string version = "1.0.0", string? publisherId = null, string sample = RevisionClient.DefaultSample)
    {
        var (capabilityId, canonical) = await NewCapabilityAsync(publisherId);
        var revision = await RevisionClient.PublishedAsync(Carol, Revisions(capabilityId, publisherId), RevisionClient.Envelope(canonical, version, sample));
        return (capabilityId, canonical, revision);
    }

    /// <summary>Runs <c>signing-key-succession</c> in-process as the owner login with the test KEK unless overridden.</summary>
    public async Task<(int Code, string Stdout, string Stderr)> SuccessionAsync(ConcurrentQueue<string>? logs, string? kek, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var provider = logs is null ? null : new GozlemeApiFactory.CapturingLoggerProvider(logs);
        string[] config = [$"--ConnectionStrings:GozlemeMigrator={Db.MigratorConnectionString}", $"--Gozleme:Signing:KeyEncryptionKey={kek ?? RevisionClient.TestKek}"];
        var code = await SigningKeyOperatorCommands.RunAsync([.. args, .. config], stdout, stderr, provider);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A direct SQL insert of a minimal revision row (no attestation), for the database-enforcement suites.</summary>
    public static string InsertRevisionSql(string org, string publisherId, string capabilityId, Guid account, int sequence = 1, string version = "1.0.0", string? versionKey = null) => $"""
        INSERT INTO gozleme.manifest_revisions (id, organization_id, publisher_id, capability_id, sequence, version, version_key, family, envelope_summary,
          native_format, native_format_version, native_encoding, native_document, native_digest, native_byte_length, projection, projection_version,
          projection_digest, adapter_id, adapter_version, rule_catalogue_version, schema_bundle_version, published_by, published_at)
        VALUES (gen_random_uuid(), '{org}', '{publisherId}', '{capabilityId}', {sequence}, '{version}', '{versionKey ?? version.Split('+')[0]}', 'mcp', '{"{}"}',
          'mcp-server-manifest', '1', 'json', convert_to('{"{}"}', 'UTF8'), 'sha256:' || encode(sha256(convert_to('{"{}"}', 'UTF8')), 'hex'), 2,
          convert_to('{"{}"}', 'UTF8'), '1', 'sha256:' || encode(sha256(convert_to('{"{}"}', 'UTF8')), 'hex'), 'mcp', '1', '1', '1', '{account}', now())
        """;

    public static string Context(Guid account) => GozlemeDatabase.Establish(GozlemeDatabase.SignContext(account));

    /// <summary>The SQLSTATE of <paramref name="sql"/> run in a rolled-back transaction after <paramref name="setup"/>.</summary>
    public static async Task<string> FailsAsync(string connection, string? setup, string sql) =>
        (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(connection, setup, sql))).SqlState;

    /// <summary>Suspends <paramref name="handle"/>'s publisher with the GOZ-0005 operator reclaim.</summary>
    public async Task ReclaimAsync(string handle)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(0, await Gozleme.Api.Identity.HandleOperatorCommands.RunAsync("handle-reclaim", ["--handle", handle, $"--ConnectionStrings:GozlemeMigrator={Db.MigratorConnectionString}"], stdout, stderr));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { Alice, Carol, Erin, Bob, BobPublisher })
        {
            client?.Dispose();
        }

        await Factory.DisposeAsync();
    }
}
