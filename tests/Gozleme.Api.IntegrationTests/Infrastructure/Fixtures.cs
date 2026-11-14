using Gozleme.Api.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(PgvectorDatabaseFixture))]

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>Shared, read-mostly database for tests that do not stop the container.</summary>
public sealed class PgvectorDatabaseFixture : IAsyncLifetime
{
    public GozlemeDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Database = await GozlemeDatabase.StartAsync();

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
}
