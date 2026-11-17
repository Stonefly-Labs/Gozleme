using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Organizations;

/// <summary>
/// Authorization decision spans and the bounded <c>gozleme.authz.decisions</c> counter (GOZ-0004 FR-018,
/// research R8). Only the action, its resource type and the outcome are recorded: never roles, account
/// ids, names, emails or codes.
/// </summary>
internal static class AuthorizationTelemetry
{
    public const string ActionTag = "gozleme.authz.action";
    public const string ResourceTypeTag = "gozleme.authz.resource_type";
    public const string OutcomeTag = "gozleme.authz.outcome";

    private static readonly Counter<long> Decisions = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.authz.decisions", unit: "{decision}", description: "Organization authorization decisions by action, resource type and outcome");

    public static void Record(string action, bool allowed)
    {
        var resource = OrgAction.ResourceOf(action);
        var outcome = allowed ? "allow" : "deny";
        using var activity = GozlemeTelemetry.ActivitySource.StartActivity("gozleme.authz.decision");
        activity?.SetTag(ActionTag, action);
        activity?.SetTag(ResourceTypeTag, resource);
        activity?.SetTag(OutcomeTag, outcome);
        Decisions.Add(1, new(ActionTag, action), new(ResourceTypeTag, resource), new(OutcomeTag, outcome));
    }
}
