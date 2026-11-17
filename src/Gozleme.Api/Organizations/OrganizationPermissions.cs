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

/// <summary>Resource types an organization action applies to (docs/architecture/contracts/organization-permissions.md).</summary>
internal static class OrgResource
{
    public const string Organization = "organization";
    public const string Membership = "membership";
    public const string Invitation = "invitation";
    public const string Publisher = "publisher";
    public const string Capability = "capability";
}

/// <summary>Organization actions (docs/architecture/contracts/organization-permissions.md). Bounded: span attributes and metric label values.</summary>
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
    public const string PublisherList = "publisher.list";
    public const string PublisherRead = "publisher.read";
    public const string PublisherRegister = "publisher.register";
    public const string PublisherRename = "publisher.rename";
    public const string CapabilityList = "capability.list";
    public const string CapabilityRead = "capability.read";
    public const string CapabilityCreate = "capability.create";
    public const string CapabilityRename = "capability.rename";
    public const string ManifestValidate = "manifest.validate";

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
        (PublisherList, OrgResource.Publisher),
        (PublisherRead, OrgResource.Publisher),
        (PublisherRegister, OrgResource.Publisher),
        (PublisherRename, OrgResource.Publisher),
        (CapabilityList, OrgResource.Capability),
        (CapabilityRead, OrgResource.Capability),
        (CapabilityCreate, OrgResource.Capability),
        (CapabilityRename, OrgResource.Capability),
        (ManifestValidate, OrgResource.Capability),
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
    [
        OrgAction.OrganizationRead, OrgAction.OrganizationSelect, OrgAction.MembershipList, OrgAction.MembershipLeave,
        OrgAction.PublisherList, OrgAction.PublisherRead, OrgAction.CapabilityList, OrgAction.CapabilityRead,
    ];

    /// <summary>
    /// An explicit list since GOZ-0005 (research R9): admin manages the organization and its publishers, but
    /// capability writes belong to <see cref="OrganizationRole.Publisher"/> alone (maintainer Q5).
    /// </summary>
    private static readonly string[] AdminActions =
    [
        .. MemberActions,
        OrgAction.OrganizationRename, OrgAction.MembershipRemove, OrgAction.MembershipRolesChange,
        OrgAction.InvitationList, OrgAction.InvitationCreate, OrgAction.InvitationRevoke,
        OrgAction.PublisherRegister, OrgAction.PublisherRename,
    ];

    /// <summary>Since GOZ-0006, <see cref="OrgAction.ManifestValidate"/> too (maintainer Q2): publisher only, admin does not hold it.</summary>
    private static readonly string[] PublisherActions = [.. MemberActions, OrgAction.CapabilityCreate, OrgAction.CapabilityRename, OrgAction.ManifestValidate];

    private static readonly FrozenDictionary<string, FrozenSet<string>> Table = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        [OrganizationRole.Admin] = AdminActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Approver] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Member] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Publisher] = PublisherActions.ToFrozenSet(StringComparer.Ordinal),
        [OrganizationRole.Reviewer] = MemberActions.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The union of the given roles' actions; an unknown role grants nothing.</summary>
    public static IReadOnlySet<string> For(IEnumerable<string> roles) =>
        roles.SelectMany(r => Table.TryGetValue(r, out var actions) ? actions : FrozenSet<string>.Empty).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The decision (FR-016): its inputs are exactly the caller's roles, the action and the action's resource
    /// type, so no condition, attribute or per-user grant can influence it. GOZ-0018 replaces this evaluation.
    /// </summary>
    public static bool Allows(IEnumerable<string> roles, string action, string resourceType) =>
        OrgAction.All.Contains((action, resourceType)) && For(roles).Contains(action);

    /// <summary>The role table as published in docs/architecture/contracts/organization-permissions.md (pinned by OrganizationPermissionContractTests).</summary>
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
