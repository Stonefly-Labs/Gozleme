using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Manifests;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Revisions;

/// <summary>Bounded publication outcomes: span tag and counter label values (research R16).</summary>
internal static class PublishOutcome
{
    public const string Published = "published";
    public const string Rejected = "rejected";
    public const string VersionExists = "version_exists";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Suspended = "suspended";
    public const string RateLimited = "rate_limited";
    public const string TooLarge = "too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The publication and verification spans and counters (GOZ-0007 FR-021, research R16) on the shared Gözleme source and
/// meter. Labels come from closed sets only (family, outcome, reason). Opaque organization, publisher, capability and
/// revision IDs go on the span only after membership is confirmed. Key material, signatures, statements, digests,
/// versions, documents, names and aliases are never recorded.
/// </summary>
internal static class RevisionTelemetry
{
    public const string PublishSpanName = "gozleme.manifest.publish";
    public const string VerifySpanName = "gozleme.manifest.verify";
    public const string ReasonTag = "gozleme.manifest.verification_reason";
    public const string RevisionIdTag = "gozleme.revision.id";
    public const string NoReason = "none";

    private static readonly Counter<long> Publications = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.manifest.publications", unit: "{publication}", description: "Manifest publications by family and bounded outcome");

    private static readonly Counter<long> Verifications = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.manifest.verifications", unit: "{verification}", description: "Revision verifications by outcome and bounded reason");

    public static Activity? StartPublish() => GozlemeTelemetry.ActivitySource.StartActivity(PublishSpanName);

    public static void Organization(Activity? activity, Guid id) => ManifestTelemetry.Organization(activity, id);

    public static void Target(Activity? activity, Guid publisherId, Guid capabilityId) => ManifestTelemetry.Target(activity, publisherId, capabilityId);

    public static void Revision(Activity? activity, Guid revisionId) => activity?.SetTag(RevisionIdTag, revisionId.ToString());

    public static void RecordPublish(Activity? activity, string outcome, string family = ManifestValidator.UnknownFamily)
    {
        activity?.SetTag(ManifestTelemetry.FamilyTag, family);
        activity?.SetTag(ManifestTelemetry.OutcomeTag, outcome);
        if (outcome is not (PublishOutcome.Published or PublishOutcome.Rejected))
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }

        Publications.Add(1, new(ManifestTelemetry.FamilyTag, family), new(ManifestTelemetry.OutcomeTag, outcome));
    }

    /// <summary>One span and one count per verification computed (every list row and detail read).</summary>
    public static void RecordVerification(Guid revisionId, Verification verification)
    {
        var reason = verification.Reason ?? NoReason;
        using (var activity = GozlemeTelemetry.ActivitySource.StartActivity(VerifySpanName))
        {
            activity?.SetTag(RevisionIdTag, revisionId.ToString());
            activity?.SetTag(ManifestTelemetry.OutcomeTag, verification.Status);
            activity?.SetTag(ReasonTag, reason);
            if (!verification.IsVerified)
            {
                activity?.SetStatus(ActivityStatusCode.Error, verification.Status);
            }
        }

        Verifications.Add(1, new(ManifestTelemetry.OutcomeTag, verification.Status), new(ReasonTag, reason));
    }
}
