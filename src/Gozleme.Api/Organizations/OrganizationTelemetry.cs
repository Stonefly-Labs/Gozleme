using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Organizations;

/// <summary>
/// Organization spans and the bounded <c>gozleme.org.operations</c> counter (research R11). The
/// organization id is set on a span only once membership is confirmed; it is never a metric label.
/// Display names, emails, account ids and invitation codes are never recorded.
/// </summary>
internal static class OrganizationTelemetry
{
    public const string OutcomeTag = "gozleme.org.outcome";
    public const string CategoryTag = "gozleme.org.failure_category";
    public const string OperationTag = "gozleme.org.operation";
    public const string OrganizationIdTag = "gozleme.organization.id";

    private static readonly Counter<long> Operations = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.org.operations", unit: "{operation}", description: "Organization operations by outcome and bounded failure category");

    public static Activity? Start(string operation) => GozlemeTelemetry.ActivitySource.StartActivity($"gozleme.org.{operation}");

    public static void Organization(Activity? activity, Guid organizationId) => activity?.SetTag(OrganizationIdTag, organizationId.ToString());

    public static void Succeeded(Activity? activity, string operation) => Record(activity, operation, "success", OrgFailure.None);

    public static void Failed(Activity? activity, string operation, string category)
    {
        Record(activity, operation, "failure", category);
        activity?.SetStatus(ActivityStatusCode.Error, category);
    }

    private static void Record(Activity? activity, string operation, string outcome, string category)
    {
        activity?.SetTag(OutcomeTag, outcome);
        activity?.SetTag(CategoryTag, category);
        Operations.Add(1, new(OperationTag, operation), new(OutcomeTag, outcome), new(CategoryTag, category));
    }
}

/// <summary>Bounded failure categories (metric label values).</summary>
internal static class OrgFailure
{
    public const string None = "none";
    public const string Validation = "validation";
    public const string NotFound = "not_found";
    public const string InvalidInvitation = "invalid_invitation";
    public const string LastMember = "last_member";
    public const string RateLimited = "rate_limited";
    public const string RefusedWrite = "refused_write";
    public const string Forbidden = "forbidden";
    public const string LastAdmin = "last_admin";
}

/// <summary>Bounded operation names (span suffixes and metric label values).</summary>
internal static class OrgOperation
{
    public const string Create = "create";
    public const string Rename = "rename";
    public const string Select = "select";
    public const string InviteCreate = "invite_create";
    public const string InviteRevoke = "invite_revoke";
    public const string InviteRedeem = "invite_redeem";
    public const string MemberRemove = "member_remove";
    public const string MemberLeave = "member_leave";
    public const string MemberRoles = "member_roles";
}
