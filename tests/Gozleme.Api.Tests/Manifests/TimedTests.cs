namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// Tests that time one validation against SC-006 run alone, after the parallel tests, so the measurement is the
/// validator's and not the test runner's load on a small CI machine.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedTests
{
    public const string Name = "Timing";
}
