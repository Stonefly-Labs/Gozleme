namespace Gozleme.Api.Children;

/// <summary>
/// Child identity composition (GOZ-0008): the store that publication and the read endpoints share. Paging options live
/// in <c>Gozleme:Manifests</c> (<c>ChildPageSize</c>) with the revision options.
/// </summary>
internal static class ChildServiceCollectionExtensions
{
    public static WebApplicationBuilder AddGozlemeChildren(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<ChildStore>();
        return builder;
    }
}
