using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Identity;

/// <summary>
/// Identity spans and the bounded <c>gozleme.auth.attempts</c> counter (research R10). Never records
/// emails, passwords, cookies, session keys or user ids; user ids appear only in audit log events.
/// </summary>
internal static class AuthTelemetry
{
    public const string OutcomeTag = "gozleme.auth.outcome";
    public const string CategoryTag = "gozleme.auth.failure_category";
    public const string OperationTag = "gozleme.auth.operation";

    private static readonly Counter<long> Attempts = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.auth.attempts", unit: "{attempt}", description: "Identity operations by outcome and bounded failure category");

    public static Activity? Start(string operation) => GozlemeTelemetry.ActivitySource.StartActivity($"gozleme.auth.{operation}");

    public static void Succeeded(Activity? activity, string operation) => Record(activity, operation, "success", "none");

    public static void Failed(Activity? activity, string operation, string category)
    {
        Record(activity, operation, "failure", category);
        activity?.SetStatus(ActivityStatusCode.Error, category);
    }

    private static void Record(Activity? activity, string operation, string outcome, string category)
    {
        activity?.SetTag(OutcomeTag, outcome);
        activity?.SetTag(CategoryTag, category);
        Attempts.Add(1, new(OperationTag, operation), new(OutcomeTag, outcome), new(CategoryTag, category));
    }
}

/// <summary>Bounded failure categories (metric label values).</summary>
internal static class AuthFailure
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string Validation = "validation";
    public const string Duplicate = "duplicate";
    public const string RegistrationClosed = "registration_closed";
    public const string RateLimited = "rate_limited";
    public const string Forbidden = "forbidden";
    public const string Unauthenticated = "unauthenticated";
}

/// <summary>Bounded operation names (span suffixes and metric label values).</summary>
internal static class AuthOperation
{
    public const string Register = "register";
    public const string SignIn = "sign_in";
    public const string SignOut = "sign_out";
    public const string SignOutAll = "sign_out_all";
}
