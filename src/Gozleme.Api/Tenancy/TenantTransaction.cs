using Gozleme.Api.Identity;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;

namespace Gozleme.Api.Tenancy;

/// <summary>
/// Wraps a tenant-scoped endpoint in one database transaction that carries the signed account context
/// of the authenticated session (research R3). The account comes only from the session principal,
/// never from a header, query value or caller-set setting (FR-008). A context that fails verification
/// is counted and audited but not short-circuited: the handler runs and RLS returns nothing.
/// A row-level refusal answers the same empty 404 as a non-member (FR-023).
/// </summary>
internal sealed class TenantTransaction(GozlemeDbContext db, TenantContextSigner signer, SecurityAudit audit) : IEndpointFilter
{
    private const string AfterCommitKey = "gozleme.tenant.after_commit";

    /// <summary>Defers an action (an audit record of a change) until the request's tenant transaction has committed.</summary>
    public static void AfterCommit(HttpContext http, Action action)
    {
        if (http.Items[AfterCommitKey] is not List<Action> actions)
        {
            http.Items[AfterCommitKey] = actions = [];
        }

        actions.Add(action);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var accountId = OrganizationAccess.CurrentUserId(context.HttpContext.User);
        using var activity = TenantTelemetry.StartTransaction();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var result = await TenantContext.EstablishAsync(db, accountId is { } id ? signer.Sign(id) : null, ct);
        TenantTelemetry.Established(activity, result);
        if (result != ContextResult.Ok)
        {
            TenantTelemetry.Denied(activity, result);
            audit.TenantContextRejected(result, accountId);
        }

        try
        {
            var response = await next(context);

            // A handler may already have rolled back (for example a lost redemption race).
            var committed = db.Database.CurrentTransaction is not null;
            if (committed)
            {
                await transaction.CommitAsync(ct);
                foreach (var action in context.HttpContext.Items[AfterCommitKey] as List<Action> ?? [])
                {
                    action();
                }
            }

            TenantTelemetry.Cleared(activity, committed ? "commit" : "rollback");
            return response;
        }
        catch (Exception ex) when (TenantContext.IsRefusal(ex))
        {
            TenantTelemetry.Cleared(activity, "rollback");
            TenantTelemetry.Denied(activity, ContextResult.RefusedWrite);
            audit.TenantWriteRefused(accountId);
            return TypedResults.NotFound();
        }
    }
}
