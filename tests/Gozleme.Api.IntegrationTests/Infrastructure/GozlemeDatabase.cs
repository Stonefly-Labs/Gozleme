using System.Net;
using System.Net.Sockets;
using System.Text;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Tenancy;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A disposable PostgreSQL initialized exactly like Compose: the repository's role bootstrap
/// script, then the one-shot migrator. Exposes connection strings for each role.
/// </summary>
public sealed class GozlemeDatabase : IAsyncDisposable
{
    public const string PgvectorImage = "pgvector/pgvector:pg17";
    public const string PlainImage = "postgres:17-alpine";
    public const string MigratorPassword = "it-migrator-Zq81";
    public const string AppPassword = "it-app-Kd47";
    public const string SuperuserPassword = "it-super-Wm63";

    /// <summary>Throwaway tenant context key (GOZ-0003), provisioned by the migrator and given to every test API.</summary>
    public const string ContextKey = "it-context-key-0003-not-a-secret-Hx52";

    /// <summary>The registry origin (GOZ-0005) recorded by the first migrate and configured for every test API.</summary>
    public const string Origin = "registry.test.gozleme.test";

    private readonly PostgreSqlContainer _container;
    private readonly int _hostPort;

    private GozlemeDatabase(PostgreSqlContainer container, int hostPort)
    {
        _container = container;
        _hostPort = hostPort;
    }

    /// <summary>Starts a dedicated container; <paramref name="migrate"/> false leaves it unmigrated (GOZ-0005 origin tests).</summary>
    public static async Task<GozlemeDatabase> StartAsync(string image = PgvectorImage, bool migrate = true)
    {
        // A fixed host port keeps connection strings valid across stop/start in outage tests.
        var hostPort = FreeTcpPort();
        var container = new PostgreSqlBuilder(image)
            .WithDatabase("gozleme")
            .WithUsername("postgres")
            .WithPassword(SuperuserPassword)
            .WithEnvironment("GOZLEME_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("GOZLEME_APP_PASSWORD", AppPassword)
            .WithResourceMapping(new FileInfo(RepoPath("deploy/postgres/init/01-roles.sh")), "/docker-entrypoint-initdb.d/")
            .WithPortBinding(hostPort, 5432)
            .Build();
        await container.StartAsync();

        var db = new GozlemeDatabase(container, hostPort);
        if (!migrate)
        {
            return db;
        }

        var exitCode = await db.MigrateAsync();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Migration failed with exit code {exitCode}.");
        }

        return db;
    }

    public string SuperuserConnectionString => Build("postgres", SuperuserPassword);

    /// <summary>The owner's own login (never <c>SET ROLE</c> from a superuser, which would satisfy the RLS internal guard).</summary>
    public string MigratorConnectionString => Build("gozleme_migrator", MigratorPassword);

    public string AppConnectionString => Build("gozleme_app", AppPassword);

    /// <summary>Runs the one-shot migrator; <paramref name="origin"/> null leaves the registry origin unset.</summary>
    public Task<int> MigrateAsync(string? origin = Origin) =>
        MigrationRunner.RunAsync([
            $"--ConnectionStrings:{MigrationRunner.MigratorConnectionStringName}={MigratorConnectionString}", $"--{TenancyOptions.Section}:ContextKey={ContextKey}",
            .. origin is null ? Array.Empty<string>() : [$"--{RegistryOriginOptions.Section}:Origin={origin}"]]);

    /// <summary>A token signed with the test key; defaults to a 5-minute lifetime.</summary>
    public static string SignContext(Guid accountId, DateTimeOffset? expires = null, string key = ContextKey) =>
        TenantContextSigner.Sign(Encoding.UTF8.GetBytes(key), accountId, expires ?? DateTimeOffset.UtcNow + TenantContextSigner.Lifetime);

    /// <summary>
    /// Runs <paramref name="sql"/> as a scalar inside one explicit transaction whose first statement is
    /// <paramref name="setup"/> (for example <c>SELECT gozleme.establish_context('…')</c>), then rolls back.
    /// RLS leak tests use this with <see cref="AppConnectionString"/> or <see cref="MigratorConnectionString"/>.
    /// </summary>
    public static async Task<T?> InTransactionAsync<T>(string connectionString, string? setup, string sql, params object[] parameters)
    {
        await using var dataSource = Named(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (setup is not null)
        {
            await using var first = new NpgsqlCommand(setup, connection, transaction);
            await first.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        var result = await command.ExecuteScalarAsync();
        await transaction.RollbackAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs <paramref name="body"/> on one open connection from a named data source (no connection string in metric labels).</summary>
    public static async Task<T> WithConnectionAsync<T>(string connectionString, Func<NpgsqlConnection, Task<T>> body)
    {
        await using var dataSource = Named(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        return await body(connection);
    }

    /// <summary>
    /// Runs <paramref name="sql"/> as the owner's own login with no account context, as the operator commands do
    /// (GOZ-0005 maintainer Q11), in one transaction that is rolled back.
    /// </summary>
    public Task<T?> AsOwnerAsync<T>(string sql, params object[] parameters) => InTransactionAsync<T>(MigratorConnectionString, null, sql, parameters);

    /// <summary>The <c>establish_context</c> setup statement for <see cref="InTransactionAsync{T}"/>.</summary>
    public static string Establish(string token) => $"SELECT gozleme.establish_context('{token}')";

    public Task StopAsync() => _container.StopAsync();

    public Task StartAgainAsync() => _container.StartAsync();

    public async Task<T> ScalarAsync<T>(string sql, string? connectionString = null)
    {
        // Named data source: test-helper connections share the process-wide Npgsql meter with the API,
        // so they must not label pools with their connection string either.
        await using var dataSource = Named(connectionString ?? SuperuserConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task ExecuteAsync(string sql, string connectionString)
    {
        await using var dataSource = Named(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static NpgsqlDataSource Named(string connectionString) =>
        new NpgsqlDataSourceBuilder(connectionString) { Name = "integration-test-fixture" }.Build();

    private string Build(string user, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = _hostPort,
        Database = "gozleme",
        Username = user,
        Password = password,
        Pooling = false,
    }.ConnectionString;

    internal static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gozleme.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
