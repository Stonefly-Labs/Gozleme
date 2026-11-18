using Gozleme.Api.Organizations;

namespace Gozleme.Api.Tests;

/// <summary>
/// GOZ-0004 FR-001–FR-003 (research R7): built-in roles, union semantics and the role table. GOZ-0005 (research R9):
/// every role reads publishers and capabilities, admin registers and renames publishers but holds no capability
/// write, and publisher alone creates and renames capabilities (maintainer Q5). GOZ-0006: publisher alone validates
/// manifests; admin does not (maintainer Q2). GOZ-0007 (maintainer Q4): resource <c>manifest</c>; publisher alone
/// publishes, every role lists and reads revisions, and <c>manifest.validate</c> moves to the new resource unchanged.
/// </summary>
public sealed class OrganizationPermissionsTests
{
    private static readonly string[] MemberActions =
    [
        OrgAction.OrganizationRead, OrgAction.OrganizationSelect, OrgAction.MembershipList, OrgAction.MembershipLeave,
        OrgAction.PublisherList, OrgAction.PublisherRead, OrgAction.CapabilityList, OrgAction.CapabilityRead,
        OrgAction.ManifestList, OrgAction.ManifestRead,
    ];

    private static readonly string[] CapabilityWrites = [OrgAction.CapabilityCreate, OrgAction.CapabilityRename, OrgAction.ManifestValidate, OrgAction.ManifestPublish];

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Reviewer)]
    [InlineData(OrganizationRole.Approver)]
    public void Member_reviewer_and_approver_hold_exactly_the_member_actions(string role) =>
        Assert.Equal(MemberActions.Order(), OrganizationPermissions.For([role]).Order());

    [Fact]
    public void Publisher_holds_the_member_actions_and_the_capability_writes() =>
        Assert.Equal(MemberActions.Concat(CapabilityWrites).Order(), OrganizationPermissions.For([OrganizationRole.Publisher]).Order());

    [Fact]
    public void Admin_holds_every_action_except_the_capability_writes()
    {
        var admin = OrganizationPermissions.For([OrganizationRole.Admin]);
        Assert.Equal(OrgAction.All.Select(a => a.Action).Except(CapabilityWrites).Order(), admin.Order());
        Assert.DoesNotContain(OrgAction.CapabilityCreate, admin);
        Assert.Contains(OrgAction.PublisherRegister, admin);
        Assert.Contains(OrgAction.PublisherRename, admin);
        Assert.False(OrganizationPermissions.Allows([OrganizationRole.Admin], OrgAction.CapabilityCreate, OrgResource.Capability));
        Assert.True(OrganizationPermissions.Allows([OrganizationRole.Admin, OrganizationRole.Publisher], OrgAction.CapabilityCreate, OrgResource.Capability));
    }

    [Fact]
    public void Manifest_validate_and_publish_are_allowed_for_publisher_only_and_every_role_reads()
    {
        foreach (var role in OrganizationRole.All)
        {
            Assert.Equal(role == OrganizationRole.Publisher, OrganizationPermissions.Allows([role], OrgAction.ManifestValidate, OrgResource.Manifest));
            Assert.Equal(role == OrganizationRole.Publisher, OrganizationPermissions.Allows([role], OrgAction.ManifestPublish, OrgResource.Manifest));
            Assert.True(OrganizationPermissions.Allows([role], OrgAction.ManifestList, OrgResource.Manifest));
            Assert.True(OrganizationPermissions.Allows([role], OrgAction.ManifestRead, OrgResource.Manifest));
        }

        // Moved: the old resource no longer matches (the decision checks the action's own resource type).
        Assert.False(OrganizationPermissions.Allows([OrganizationRole.Publisher], OrgAction.ManifestValidate, OrgResource.Capability));
        Assert.Equal(OrgResource.Manifest, OrgAction.ResourceOf(OrgAction.ManifestValidate));
        Assert.Equal(22, OrgAction.All.Count);
    }

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
        Assert.All(OrgAction.All, a => Assert.Contains(a.Resource, new[] { OrgResource.Organization, OrgResource.Membership, OrgResource.Invitation, OrgResource.Publisher, OrgResource.Capability, OrgResource.Manifest }));
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
