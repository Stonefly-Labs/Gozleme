namespace Gozleme.Api.Identity;

/// <summary>
/// CSRF layer 2 (research R5): unsafe calls must carry <c>x-gozleme-client: web</c>. Browsers cannot
/// add a custom header cross-site without a CORS preflight, and the API grants none. Applied to the
/// whole <c>/api/v1</c> group; safe methods pass through.
/// </summary>
internal sealed class WebClientHeaderFilter : IEndpointFilter
{
    public const string HeaderName = "x-gozleme-client";
    public const string HeaderValue = "web";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return await next(context);
        }

        var headers = context.HttpContext.Request.Headers[HeaderName];
        if (headers.Count != 1 || !string.Equals(headers[0], HeaderValue, StringComparison.Ordinal))
        {
            return TypedResults.Json(new AuthError(AuthError.Forbidden), statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
