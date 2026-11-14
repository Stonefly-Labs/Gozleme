using System.Net;
using System.Net.Sockets;
using Gozleme.Api.Persistence;
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

    private readonly PostgreSqlContainer _container;
    private readonly int _hostPort;

    private GozlemeDatabase(PostgreSqlContainer container, int hostPort)
    {
        _container = container;
        _hostPort = hostPort;
    }

    public static async Task<GozlemeDatabase> StartAsync(string image = PgvectorImage)
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
        var exitCode = await db.MigrateAsync();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Migration failed with exit code {exitCode}.");
        }

        return db;
    }

    public string SuperuserConnectionString => Build("postgres", SuperuserPassword);

    public string MigratorConnectionString => Build("gozleme_migrator", MigratorPassword);

    public string AppConnectionString => Build("gozleme_app", AppPassword);

    public Task<int> MigrateAsync() =>
        MigrationRunner.RunAsync([$"--ConnectionStrings:{MigrationRunner.MigratorConnectionStringName}={MigratorConnectionString}"]);

    public Task StopAsync() => _container.StopAsync();

    public Task StartAgainAsync() => _container.StartAsync();

    public async Task<T> ScalarAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task ExecuteAsync(string sql, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

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
