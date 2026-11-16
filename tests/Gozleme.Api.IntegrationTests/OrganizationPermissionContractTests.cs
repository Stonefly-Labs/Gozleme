using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Organizations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0004 US2 (FR-003, FR-006, SC-002): every organization-scoped endpoint declares the actions it
/// authorizes, every action has a role-table entry, and the code's table is exactly the published contract.
/// </summary>
public sealed class OrganizationPermissionContractTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Scoped = "api/v1/organizations/{organizationId}";

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        _ = _factory.Server;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void Every_organization_scoped_endpoint_declares_known_actions()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.Equal(9, endpoints.Count(IsScoped)); // read, select, rename, members, remove/leave, roles, invitations ×3
        Assert.Empty(Undeclared(endpoints));

        var declared = endpoints.Where(IsScoped).SelectMany(e => e.Metadata.GetMetadata<OrgActionAttribute>()!.Actions).Order(StringComparer.Ordinal);
        Assert.Equal(OrgAction.All.Select(a => a.Action).Order(StringComparer.Ordinal), declared);
    }

    [Fact]
    public void An_endpoint_mapped_without_its_action_is_reported()
    {
        RouteEndpoint[] endpoints =
        [
            Endpoint($"/{Scoped}/extra"),
            Endpoint($"/{Scoped}/typo", new OrgActionAttribute("organization.delete")),
            Endpoint("/api/v1/organizations"), // account-level, outside the table
        ];
        Assert.Equal([$"/{Scoped}/extra", $"/{Scoped}/typo"], Undeclared(endpoints));
    }

    [Fact]
    public void Every_action_has_a_resource_and_a_role_table_row()
    {
        foreach (var (action, resource) in OrgAction.All)
        {
            Assert.Contains(resource, new[] { OrgResource.Organization, OrgResource.Membership, OrgResource.Invitation });
            Assert.Contains(OrganizationPermissions.RenderTable().Split('\n'), line => line.StartsWith(action + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_code_renders_exactly_the_published_role_table()
    {
        var contract = File.ReadAllText(GozlemeDatabase.RepoPath("docs/specs/004-scoped-rbac/contracts/permissions.md"));
        var start = contract.IndexOf("```text\n", StringComparison.Ordinal) + "```text\n".Length;
        var published = contract[start..contract.IndexOf("```", start, StringComparison.Ordinal)];
        Assert.Equal(published.TrimEnd('\n'), OrganizationPermissions.RenderTable().TrimEnd('\n'));
    }

    private static bool IsScoped(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith(Scoped, StringComparison.Ordinal) == true;

    private static string[] Undeclared(IEnumerable<RouteEndpoint> endpoints) =>
    [
        .. endpoints.Where(IsScoped)
            .Where(e => e.Metadata.GetMetadata<OrgActionAttribute>() is not { Actions.Count: > 0 } declared
                || declared.Actions.Any(a => OrgAction.All.All(known => known.Action != a)))
            .Select(e => e.RoutePattern.RawText!),
    ];

    private static RouteEndpoint Endpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);
}
