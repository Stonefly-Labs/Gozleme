using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// TestServer has no socket peer; this sets <c>Connection.RemoteIpAddress</c> from <c>x-test-peer</c>
/// ahead of the real pipeline so forwarded-header trust can be exercised.
/// </summary>
public sealed class TestPeerAddress : IStartupFilter
{
    public const string Header = "x-test-peer";

    public static void Register(IServiceCollection services) => services.AddTransient<IStartupFilter, TestPeerAddress>();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((HttpContext http, RequestDelegate nextDelegate) =>
        {
            if (IPAddress.TryParse(http.Request.Headers[Header], out var peer))
            {
                http.Connection.RemoteIpAddress = peer;
            }

            return nextDelegate(http);
        });
        next(app);
    };
}
