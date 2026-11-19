using Gozleme.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// Deny by default (Constitution IV, research R5): every mapped endpoint carries an explicit
/// authorization decision, and the anonymous ones are exactly the deliberate opt-outs. A new endpoint
/// mapped outside the <c>/api/v1</c> group, or a new <c>AllowAnonymous</c>, fails here until reviewed.
/// </summary>
public sealed class EndpointAuthorizationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] AnonymousEndpoints =
    [
        "GET /api/v1/auth/options",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/sign-in",
        "POST /api/v1/auth/sign-out",
    ];

    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        _ = _factory.Server; // build the host so endpoints are registered
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void Every_endpoint_requires_a_session_unless_it_is_a_deliberate_anonymous_opt_out()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        var anonymous = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var name = $"{string.Join(',', endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
            Assert.True(endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null, $"{name} has no authorization decision");
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                anonymous.Add(name);
            }
        }

        Assert.Equal(AnonymousEndpoints, anonymous.Order(StringComparer.Ordinal));
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "/api/v1/organizations/{organizationId}/members/{userId}/roles"
            && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.SequenceEqual(["PUT"])
            && e.Metadata.GetMetadata<IAllowAnonymous>() is null); // GOZ-0004: session required

        // GOZ-0008: the five child and projection routes are GET only and require a session.
        const string capability = "/api/v1/organizations/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}";
        foreach (var route in new[]
        {
            capability + "/children", capability + "/children/{childId}", capability + "/revisions/{revisionId}/children",
            capability + "/revisions/{revisionId}/projections", capability + "/revisions/{revisionId}/projections/{generationId}",
        })
        {
            var mapped = Assert.Single(endpoints, e => e.RoutePattern.RawText == route);
            Assert.Equal(["GET"], mapped.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
            Assert.NotNull(mapped.Metadata.GetMetadata<IAuthorizeData>());
            Assert.Null(mapped.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }
}
