using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Manifests;

/// <summary>
/// The validation span and counters (GOZ-0006 FR-022, research R16) on the shared Gözleme source and meter. Labels
/// come from closed sets only. Opaque organization, publisher and capability IDs are set on the span only after
/// membership is confirmed. No pointer, rule, name, document, projection or digest is ever recorded.
/// </summary>
internal static class ManifestTelemetry
{
    public const string SpanName = "gozleme.manifest.validate";
    public const string FamilyTag = "gozleme.manifest.family";
    public const string OutcomeTag = "gozleme.manifest.outcome";
    public const string ProblemCountTag = "gozleme.manifest.problem_count";
    public const string CategoryTag = "gozleme.manifest.category";
    public const string OrganizationIdTag = "gozleme.organization.id";
    public const string PublisherIdTag = "gozleme.publisher.id";
    public const string CapabilityIdTag = "gozleme.capability.id";

    private static readonly Counter<long> Validations = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.manifest.validations", unit: "{validation}", description: "Manifest validations by family and bounded outcome");

    private static readonly Counter<long> Problems = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.manifest.problems", unit: "{category}", description: "Rejected manifest validations by family and problem category, once per distinct category");

    public static Activity? Start() => GozlemeTelemetry.ActivitySource.StartActivity(SpanName);

    public static void Organization(Activity? activity, Guid id) => activity?.SetTag(OrganizationIdTag, id.ToString());

    public static void Target(Activity? activity, Guid publisherId, Guid capabilityId)
    {
        activity?.SetTag(PublisherIdTag, publisherId.ToString());
        activity?.SetTag(CapabilityIdTag, capabilityId.ToString());
    }

    /// <summary>Records an answer that ran no validation (authorization, transport or rate-limit outcomes).</summary>
    public static void Record(Activity? activity, string outcome) => Record(activity, ManifestValidator.UnknownFamily, outcome, 0, []);

    public static void Record(Activity? activity, ValidationRun run) =>
        Record(activity, run.Family, run.Result.Outcome, run.Result is RejectedResult r ? r.Problems.Count : 0, run.Categories);

    private static void Record(Activity? activity, string family, string outcome, int problemCount, IReadOnlyList<ProblemCategory> categories)
    {
        activity?.SetTag(FamilyTag, family);
        activity?.SetTag(OutcomeTag, outcome);
        activity?.SetTag(ProblemCountTag, problemCount);
        if (outcome is not (ManifestOutcome.Accepted or ManifestOutcome.Rejected))
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }

        Validations.Add(1, new(FamilyTag, family), new(OutcomeTag, outcome));
        foreach (var category in categories)
        {
            Problems.Add(1, new(FamilyTag, family), new(CategoryTag, ManifestRules.Name(category)));
        }
    }
}
