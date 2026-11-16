using System.Data.Common;
using System.Diagnostics;
using Gozleme.Api.Telemetry;
using Gozleme.Api.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gozleme.Api.Organizations;

/// <summary>
/// Closes the enclosing <c>gozleme.org.*</c> span as a <c>refused_write</c> failure when row-level
/// security refuses a command. The refusal unwinds through the handler, which ends its span before
/// <see cref="TenantTransaction"/> answers "not found", so the outcome must be recorded here, while
/// the span is still open (GOZ-0002 R11: every organization span carries a bounded outcome).
/// </summary>
internal sealed class RefusedWriteSpanInterceptor : DbCommandInterceptor
{
    public static readonly RefusedWriteSpanInterceptor Instance = new();

    private const string SpanPrefix = "gozleme.org.";

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Record(eventData.Exception);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Record(eventData.Exception);
        return Task.CompletedTask;
    }

    private static void Record(Exception exception)
    {
        if (!TenantContext.IsRefusal(exception))
        {
            return;
        }

        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (activity.Source == GozlemeTelemetry.ActivitySource && activity.OperationName.StartsWith(SpanPrefix, StringComparison.Ordinal))
            {
                if (activity.GetTagItem(OrganizationTelemetry.OutcomeTag) is null)
                {
                    OrganizationTelemetry.Failed(activity, activity.OperationName[SpanPrefix.Length..], OrgFailure.RefusedWrite);
                }

                return;
            }
        }
    }
}
