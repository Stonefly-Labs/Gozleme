using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Npgsql;

namespace Gozleme.Api.Tenancy;

/// <summary>Tenancy composition (GOZ-0003): the validated context key and its signer.</summary>
internal static class TenancyServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddGozlemeTenancy(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<TenancyOptions>().Bind(builder.Configuration.GetSection(TenancyOptions.Section))
            .Validate(o => o.IsValid, $"{TenancyOptions.Section}:ContextKey must be set to at least {TenancyOptions.MinKeyLength} characters.")
            .ValidateOnStart();
        builder.Services.AddSingleton<TenantContextSigner>();
        return builder;
    }

    /// <summary>
    /// Startup self-check (research R9): a key that differs from the one the migrator provisioned would
    /// make every organization look missing, so report it at once. Never blocks startup or serving.
    /// </summary>
    public static WebApplicationBuilder AddTenantContextSelfCheck(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<TenantContextSelfCheck>();
        return builder;
    }

    private sealed class TenantContextSelfCheck(IServiceScopeFactory scopes, TenantContextSigner signer, SecurityAudit audit) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GozlemeDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                var result = await TenantContext.EstablishAsync(db, signer.Sign(Guid.Empty), stoppingToken);
                if (result != ContextResult.Ok)
                {
                    TenantTelemetry.Denied(null, result);
                    audit.TenantContextSelfCheckFailed(result);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException)
            {
                // Database down or shutting down: readiness and request paths already report it.
            }
        }
    }
}
