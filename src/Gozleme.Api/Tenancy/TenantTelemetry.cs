using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Tenancy;

/// <summary>
/// The <c>gozleme.tenant.transaction</c> span and the bounded <c>gozleme.tenant.denials</c> counter
/// (research R9). No organization id at this level (membership is not yet confirmed); never the
/// token, the key, a code hash or an account id as a metric label.
/// </summary>
internal static class TenantTelemetry
{
    public const string TransactionSpan = "gozleme.tenant.transaction";
    public const string EstablishedEvent = "gozleme.tenant.context.established";
    public const string ClearedEvent = "gozleme.tenant.context.cleared";
    public const string ResultTag = "gozleme.tenant.context.result";
    public const string OutcomeTag = "gozleme.tenant.transaction.outcome";
    public const string ReasonTag = "gozleme.tenant.denial.reason";

    private static readonly Counter<long> Denials = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.tenant.denials", unit: "{denial}", description: "Rejected tenant contexts and row-level security refusals by bounded reason");

    public static Activity? StartTransaction() => GozlemeTelemetry.ActivitySource.StartActivity(TransactionSpan);

    public static void Established(Activity? activity, string result) =>
        activity?.AddEvent(new ActivityEvent(EstablishedEvent, tags: new ActivityTagsCollection { [ResultTag] = result }));

    public static void Cleared(Activity? activity, string outcome) =>
        activity?.AddEvent(new ActivityEvent(ClearedEvent, tags: new ActivityTagsCollection { [OutcomeTag] = outcome }));

    public static void Denied(Activity? activity, string reason)
    {
        Denials.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));
        activity?.SetStatus(ActivityStatusCode.Error, reason);
    }
}

/// <summary>Results of <c>gozleme.establish_context</c>, plus the refused-write reason (metric label values).</summary>
internal static class ContextResult
{
    public const string Ok = "ok";
    public const string Missing = "missing";
    public const string Invalid = "invalid";
    public const string Expired = "expired";
    public const string RefusedWrite = "refused_write";
}
