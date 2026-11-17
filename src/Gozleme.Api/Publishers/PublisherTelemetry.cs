using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Publishers;

/// <summary>
/// Publisher spans and the bounded <c>gozleme.publisher.operations</c> counter (GOZ-0005 FR-026, research R10).
/// Opaque ids are set on a span only after membership is confirmed and are never metric labels. Handles,
/// capability names, aliases, display names and emails are never recorded.
/// </summary>
internal static class PublisherTelemetry
{
    public const string OperationTag = "gozleme.publisher.operation";
    public const string OutcomeTag = "gozleme.publisher.outcome";
    public const string OrganizationIdTag = "gozleme.organization.id";
    public const string PublisherIdTag = "gozleme.publisher.id";
    public const string CapabilityIdTag = "gozleme.capability.id";
    public const string ExitCodeTag = "gozleme.operator.exit_code";

    private static readonly Counter<long> Operations = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.publisher.operations", unit: "{operation}", description: "Publisher and capability writes by operation and bounded outcome");

    /// <summary>Starts <c>gozleme.publisher.register|rename</c> or <c>gozleme.capability.create|rename</c>.</summary>
    public static Activity? Start(string operation) => GozlemeTelemetry.ActivitySource.StartActivity(operation switch
    {
        PublisherOperation.Register => "gozleme.publisher.register",
        PublisherOperation.Rename => "gozleme.publisher.rename",
        PublisherOperation.CapabilityCreate => "gozleme.capability.create",
        _ => "gozleme.capability.rename",
    });

    public static void Organization(Activity? activity, Guid organizationId) => activity?.SetTag(OrganizationIdTag, organizationId.ToString());

    public static void Publisher(Activity? activity, Guid publisherId) => activity?.SetTag(PublisherIdTag, publisherId.ToString());

    public static void Capability(Activity? activity, Guid capabilityId) => activity?.SetTag(CapabilityIdTag, capabilityId.ToString());

    public static void Record(Activity? activity, string operation, string outcome)
    {
        activity?.SetTag(OutcomeTag, outcome);
        if (outcome is not (PublisherOutcome.Success or PublisherOutcome.Unchanged))
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }

        Operations.Add(1, new(OperationTag, operation), new(OutcomeTag, outcome));
    }

    /// <summary>Starts <c>gozleme.operator.handle_reserve|handle_release|handle_reclaim</c>; only the exit code is recorded.</summary>
    public static Activity? StartOperator(string command) =>
        GozlemeTelemetry.ActivitySource.StartActivity($"gozleme.operator.{command.Replace('-', '_')}");
}

/// <summary>Bounded operation names (metric label values).</summary>
internal static class PublisherOperation
{
    public const string Register = "register";
    public const string Rename = "rename";
    public const string CapabilityCreate = "capability_create";
    public const string CapabilityRename = "capability_rename";
}

/// <summary>Bounded outcomes (metric label values).</summary>
internal static class PublisherOutcome
{
    public const string Success = "success";
    public const string Unchanged = "unchanged";
    public const string ValidationFailed = "validation_failed";
    public const string HandleUnavailable = "handle_unavailable";
    public const string Conflict = "conflict";
    public const string Forbidden = "forbidden";
    public const string Suspended = "suspended";
    public const string NotFound = "not_found";
    public const string RateLimited = "rate_limited";
}
