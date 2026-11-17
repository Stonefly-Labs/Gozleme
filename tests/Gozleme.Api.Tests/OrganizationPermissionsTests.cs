using Gozleme.Api.Organizations;

namespace Gozleme.Api.Tests;

/// <summary>GOZ-0004 FR-001–FR-003 (research R7): built-in roles, union semantics and the role table.</summary>
public sealed class OrganizationPermissionsTests
{
    private static readonly string[] MemberActions =
        [OrgAction.OrganizationRead, OrgAction.OrganizationSelect, OrgAction.MembershipList, OrgAction.MembershipLeave];

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Publisher)]
    [InlineData(OrganizationRole.Reviewer)]
    [InlineData(OrganizationRole.Approver)]
    public void Non_admin_roles_hold_exactly_the_member_actions(string role) =>
        Assert.Equal(MemberActions.Order(), OrganizationPermissions.For([role]).Order());

    [Fact]
    public void Admin_holds_every_action() =>
        Assert.Equal(OrgAction.All.Select(a => a.Action).Order(), OrganizationPermissions.For([OrganizationRole.Admin]).Order());

    [Fact]
    public void Permissions_are_the_union_of_the_roles_and_unknown_roles_grant_nothing()
    {
        Assert.Equal(MemberActions.Order(), OrganizationPermissions.For([OrganizationRole.Member, OrganizationRole.Reviewer, "owner"]).Order());
        Assert.Empty(OrganizationPermissions.For(["owner", "root"]));
        Assert.Empty(OrganizationPermissions.For([]));
    }

    [Fact]
    public void No_role_implies_another()
    {
        // Admin's rights come from its own row, not from holding publisher, reviewer or approver (Q4 = A).
        string[] expected = [OrganizationRole.Admin, OrganizationRole.Member];
        Assert.Equal(expected, OrganizationRole.Normalize([OrganizationRole.Admin]));
    }

    [Theory]
    [InlineData(new[] { "admin" }, new[] { "admin", "member" })]
    [InlineData(new[] { "reviewer", "member", "publisher", "reviewer" }, new[] { "member", "publisher", "reviewer" })]
    [InlineData(new string[0], new[] { "member" })]
    public void Role_sets_are_stored_distinct_sorted_and_with_member(string[] input, string[] expected) =>
        Assert.Equal(expected, OrganizationRole.Normalize(input));

    [Theory]
    [InlineData("owner")]
    [InlineData("Admin")]
    [InlineData("")]
    public void Unknown_role_names_are_rejected(string role) => Assert.Null(OrganizationRole.Normalize([OrganizationRole.Member, role]));

    [Fact]
    public void Every_action_has_a_resource_type_and_the_table_lists_every_action()
    {
        Assert.All(OrgAction.All, a => Assert.Contains(a.Resource, new[] { OrgResource.Organization, OrgResource.Membership, OrgResource.Invitation }));
        var table = OrganizationPermissions.RenderTable();
        Assert.All(OrgAction.All, a => Assert.Contains($"\n{a.Action} ", table));
    }

    [Fact]
    public void Allows_needs_a_permitting_role_and_the_actions_own_resource_type()
    {
        Assert.True(OrganizationPermissions.Allows([OrganizationRole.Admin], OrgAction.OrganizationRename, OrgResource.Organization));
        Assert.False(OrganizationPermissions.Allows([OrganizationRole.Member], OrgAction.OrganizationRename, OrgResource.Organization));
        Assert.False(OrganizationPermissions.Allows([OrganizationRole.Admin], OrgAction.OrganizationRename, OrgResource.Membership));
        Assert.False(OrganizationPermissions.Allows([OrganizationRole.Admin], "organization.delete", OrgResource.Organization));
    }
}
