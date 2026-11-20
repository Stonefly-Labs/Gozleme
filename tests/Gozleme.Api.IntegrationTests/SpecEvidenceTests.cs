using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>GOZ-0008-IF-16: test runs never rewrite the evidence of a spec the registry marks complete.</summary>
public sealed class SpecEvidenceTests
{
    [Fact]
    public void CompletedSpecEvidenceIsFrozen()
    {
        Assert.True(SpecEvidence.IsComplete("GOZ-0006"));
        Assert.True(SpecEvidence.IsComplete("GOZ-0007"));
        Assert.False(SpecEvidence.IsComplete("GOZ-0041"));
    }
}
