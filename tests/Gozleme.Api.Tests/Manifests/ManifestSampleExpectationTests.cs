using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>GOZ-0006 research R15: every bundled sample gives exactly its expected result through the pure validator.</summary>
public sealed class ManifestSampleExpectationTests
{
    public static TheoryData<string> Samples => [.. ManifestSamples.All.Select(s => s.Id)];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Each_sample_gives_exactly_its_expected_problems(string id)
    {
        var sample = ManifestSamples.Get(id);
        var result = Validate(sample.Body(CanonicalId));
        if (sample.Expected["outcome"]!.GetValue<string>() == "accepted")
        {
            Accepted(result);
        }
        else
        {
            Assert.Equal(sample.ExpectedProblems(), Problems(result));
        }
    }
}
