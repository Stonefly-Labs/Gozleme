namespace Gozleme.Api.Identity;

/// <summary>
/// CSRF layer 2 (research R5): unsafe calls must carry <c>x-gozleme-client: web</c>. Browsers cannot
/// add a custom header cross-site without a CORS preflight, and the API grants none.
/// </summary>
internal sealed class WebClientHeaderFilter : IEndpointFilter
{
    public const string HeaderName = "x-gozleme-client";
    public const string HeaderValue = "web";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers[HeaderName];
        if (headers.Count != 1 || !string.Equals(headers[0], HeaderValue, StringComparison.Ordinal))
        {
            return TypedResults.Json(new AuthError(AuthError.Forbidden), statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
