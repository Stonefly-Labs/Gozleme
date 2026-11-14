using Gozleme.Api.Status;
using Microsoft.Extensions.Configuration;

namespace Gozleme.Api.Tests;

public sealed class StatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static DependencyStatus Dep(string status, bool required = true) =>
        new("database", status, required, null, null, null, null);

    [Fact]
    public void All_required_dependencies_healthy_is_healthy() =>
        Assert.Equal(StatusValues.Healthy, StatusService.Aggregate([Dep(StatusValues.Healthy)]));

    [Fact]
    public void Unavailable_required_dependency_is_degraded() =>
        Assert.Equal(StatusValues.Degraded, StatusService.Aggregate([Dep(StatusValues.Healthy), Dep(StatusValues.Unavailable)]));

    [Fact]
    public void Unavailable_optional_dependency_does_not_degrade() =>
        Assert.Equal(StatusValues.Healthy, StatusService.Aggregate([Dep(StatusValues.Healthy), Dep(StatusValues.Unavailable, required: false)]));

    [Fact]
    public async Task Status_document_uses_contract_v1_configured_version_and_clock()
    {
        var service = new StatusService(new FakeProbe(Dep(StatusValues.Unavailable)), new FixedTime(Now), Config(("Gozleme:BuildVersion", "1.2.3-test")));

        var status = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1", status.ContractVersion);
        Assert.Equal("gozleme-api", status.Service);
        Assert.Equal("1.2.3-test", status.Version);
        Assert.Equal(StatusValues.Degraded, status.Status);
        Assert.Equal(Now, status.CheckedAt);
        Assert.Single(status.Dependencies);
    }

    [Fact]
    public async Task Version_falls_back_to_assembly_version_when_not_configured()
    {
        var service = new StatusService(new FakeProbe(Dep(StatusValues.Healthy)), new FixedTime(Now), Config());
        var status = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class FakeProbe(DependencyStatus result) : IDatabaseProbe
    {
        public Task<DependencyStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
