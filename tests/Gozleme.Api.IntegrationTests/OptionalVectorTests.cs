using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>FR-010: the stack is healthy on PostgreSQL without the optional pgvector extension.</summary>
public sealed class OptionalVectorTests
{
    [Fact]
    public async Task Plain_postgres_without_pgvector_is_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await GozlemeDatabase.StartAsync(GozlemeDatabase.PlainImage);
        await using var factory = new GozlemeApiFactory(db.AppConnectionString);
        var (client, _) = await AuthClient.SignedInAsync(factory);
        using var _ = client;

        var root = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/v1/status", UriKind.Relative), ct)).RootElement;

        Assert.Equal("healthy", root.GetProperty("status").GetString());
        var vector = root.GetProperty("dependencies")[0].GetProperty("capabilities").GetProperty("vector");
        Assert.False(vector.GetProperty("available").GetBoolean());
        Assert.False(vector.GetProperty("enabled").GetBoolean());
    }
}
