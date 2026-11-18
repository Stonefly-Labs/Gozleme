using System.ComponentModel.DataAnnotations;
using Gozleme.Api.Manifests.Adapters;

namespace Gozleme.Api.Manifests;

/// <summary>
/// Manifest validation composition (GOZ-0006): the offline schema registry and its non-blocking warm-up, the six
/// stateless adapters, the pure validator, the per-account limiter and its validated options (research R2, R11).
/// </summary>
internal static class ManifestServiceCollectionExtensions
{
    public static WebApplicationBuilder AddGozlemeManifests(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ManifestOptions>().Bind(builder.Configuration.GetSection(ManifestOptions.Section))
            .Validate(o => Validator.TryValidateObject(o, new ValidationContext(o), null, validateAllProperties: true), $"{ManifestOptions.Section} values are outside their safe bounds.")
            .ValidateOnStart();
        builder.Services.AddSingleton<SchemaRegistry>();
        builder.Services.AddHostedService<SchemaWarmUp>();
        foreach (var adapter in ManifestAdapters.All)
        {
            builder.Services.AddSingleton(adapter);
        }

        builder.Services.AddSingleton<ManifestValidator>();
        builder.Services.AddSingleton<ManifestValidationLimiter>();
        return builder;
    }
}
