using System.Collections.Frozen;
using System.Text;

namespace Gozleme.Api.Organizations;

/// <summary>Built-in organization roles (GOZ-0004 FR-001). Not user-editable; every membership holds <see cref="Member"/>.</summary>
internal static class OrganizationRole
{
    public const string Admin = "admin";
    public const string Approver = "approver";
    public const string Member = "member";
    public const string Publisher = "publisher";
    public const string Reviewer = "reviewer";

    public static readonly IReadOnlyList<string> All = [Admin, Approver, Member, Publisher, Reviewer];

    /// <summary>The stored form of a role set: distinct, sorted, always containing <see cref="Member"/>. Null when a name is unknown.</summary>
    public static string[]? Normalize(IEnumerable<string?>? roles)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal) { Member };
        foreach (var role in roles ?? [])
        {
            if (role is null || !All.Contains(role, StringComparer.Ordinal))
            {
                return null;
            }

            set.Add(role);
        }

        return [.. set];
    }
}

/// <summary>Resource types an organization action applies to (contracts/permissions.md).</summary>
internal static class OrgResource
{
    public const string Organization = "organization";
    public const string Membership = "membership";
    public const string Invitation = "invitation";
}

/// <summary>Organization actions (contracts/permissions.md). Bounded: span attributes and metric label values.</summary>
internal static class OrgAction
{
    public const string OrganizationRead = "organization.read";
    public const string OrganizationSelect = "organization.select";
    public const string OrganizationRename = "organization.rename";
    public const string MembershipList = "membership.list";
    public const string MembershipLeave = "membership.leave";
    public const string MembershipRemove = "membership.remove";
    public const string MembershipRolesChange = "membership.roles.change";
    public const string InvitationList = "invitation.list";
    public const string InvitationCreate = "invitation.create";
    public const string InvitationRevoke = "invitation.revoke";

    /// <summary>Every action with its resource type, in contract order.</summary>
    public static readonly IReadOnlyList<(string Action, string Resource)> All =
    [
        (OrganizationRead, OrgResource.Organization),
        (OrganizationSelect, OrgResource.Organization),
        (OrganizationRename, OrgResource.Organization),
        (MembershipList, OrgResource.Membership),
        (MembershipLeave, OrgResource.Membership),
        (MembershipRemove, OrgResource.Membership),
        (MembershipRolesChange, OrgResource.Membership),
        (InvitationList, OrgResource.Invitation),
        (InvitationCreate, OrgResource.Invitation),
        (InvitationRevoke, OrgResource.Invitation),
    ];

    public static string ResourceOf(string action) => All.Single(a => a.Action == action).Resource;
}

/// <summary>
/// The built-in role table (GOZ-0004 FR-003, research R7). A caller's permissions are the union of its
/// roles' actions; no role implies another. GOZ-0018 replaces the evaluation behind the same decision point.
/// </summary>
internal static class OrganizationPermissions
{
    private static readonly string[] MemberActions =
        [OrgAction.OrganizationRead, OrgAction.OrganizationSelect, OrgAction.MembershipList, OrgAction.MembershipLeave];

    private static readonly FrozenDictionary<string, FrozenSet<string>> Table = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        [OrganizationRole.Admin] = OrgAction.All.Select(a => a.Action).ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Approver] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Member] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Publisher] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Reviewer] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The union of the given roles' actions; an unknown role grants nothing.</summary>
    public static IReadOnlySet<string> For(IEnumerable<string> roles) =>
        roles.SelectMany(r => Table.TryGetValue(r, out var actions) ? actions : FrozenSet<string>.Empty).ToHashSet(StringComparer.Ordinal);

    /// <summary>The role table as published in contracts/permissions.md (pinned by OrganizationPermissionContractTests).</summary>
    public static string RenderTable()
    {
        const int actionWidth = 25, resourceWidth = 14;
        var text = new StringBuilder();
        text.Append("action".PadRight(actionWidth)).Append("resource".PadRight(resourceWidth));
        text.AppendJoin("  ", OrganizationRole.All.Select((r, i) => i == OrganizationRole.All.Count - 1 ? r : r.PadRight(Math.Max(r.Length, 5)))).Append('\n');
        foreach (var (action, resource) in OrgAction.All)
        {
            text.Append(action.PadRight(actionWidth)).Append(resource.PadRight(resourceWidth));
            text.AppendJoin("  ", OrganizationRole.All.Select((r, i) =>
            {
                var cell = Table[r].Contains(action) ? "allow" : "deny";
                return i == OrganizationRole.All.Count - 1 ? cell : cell.PadRight(Math.Max(r.Length, 5));
            })).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>Endpoint metadata: the organization action(s) an endpoint performs (FR-006).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
internal sealed class OrgActionAttribute(params string[] actions) : Attribute
{
    public IReadOnlyList<string> Actions { get; } = actions;
}
