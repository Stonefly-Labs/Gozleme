using Gozleme.Api.Revisions.Signing;

namespace Gozleme.Api.Revisions;

/// <summary>
/// Signed revision composition (GOZ-0007): the key vault over <c>Gozleme:Signing</c> (not validated on start: without a
/// usable KEK publication answers 503 and reads still work, research R6), the store, the publication transaction and
/// the shared publication budget. The publication options live in <c>Gozleme:Manifests</c> with validation's.
/// </summary>
internal static class RevisionServiceCollectionExtensions
{
    public static WebApplicationBuilder AddGozlemeRevisions(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<SigningOptions>().Bind(builder.Configuration.GetSection(SigningOptions.Section));
        builder.Services.AddSingleton<SigningKeyVault>();
        builder.Services.AddScoped<RevisionStore>();
        builder.Services.AddScoped<RevisionPublisher>();
        builder.Services.AddSingleton<RevisionPublicationLimiter>();
        return builder;
    }
}
