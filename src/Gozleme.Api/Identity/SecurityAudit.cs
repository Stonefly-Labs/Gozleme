namespace Gozleme.Api.Identity;

/// <summary>
/// Security audit events (FR-029): category <c>Gozleme.Security.Audit</c>, opaque account id only —
/// never an email, password, cookie or session key. Exported through the normal log pipeline.
/// </summary>
internal sealed partial class SecurityAudit(ILoggerFactory loggers)
{
    public const string Category = "Gozleme.Security.Audit";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    [LoggerMessage(EventId = 1001, EventName = "gozleme.audit.registered", Level = LogLevel.Information, Message = "Account registered {UserId}")]
    public partial void Registered(Guid userId);

    [LoggerMessage(EventId = 1002, EventName = "gozleme.audit.registration_refused", Level = LogLevel.Information, Message = "Registration refused ({Category})")]
    public partial void RegistrationRefused(string category);

    [LoggerMessage(EventId = 1003, EventName = "gozleme.audit.signed_in", Level = LogLevel.Information, Message = "Signed in {UserId}")]
    public partial void SignedIn(Guid userId);

    [LoggerMessage(EventId = 1004, EventName = "gozleme.audit.sign_in_failed", Level = LogLevel.Warning, Message = "Sign-in failed ({Category}) {UserId}")]
    public partial void SignInFailed(string category, Guid? userId);

    [LoggerMessage(EventId = 1005, EventName = "gozleme.audit.locked_out", Level = LogLevel.Warning, Message = "Account locked out; sessions revoked {UserId}")]
    public partial void LockedOut(Guid userId);

    [LoggerMessage(EventId = 1006, EventName = "gozleme.audit.signed_out", Level = LogLevel.Information, Message = "Signed out {UserId}")]
    public partial void SignedOut(Guid? userId);

    [LoggerMessage(EventId = 1007, EventName = "gozleme.audit.signed_out_all", Level = LogLevel.Information, Message = "Signed out everywhere {UserId}")]
    public partial void SignedOutAll(Guid userId);

    [LoggerMessage(EventId = 1008, EventName = "gozleme.audit.operator_reset", Level = LogLevel.Warning, Message = "Operator password reset ({Outcome}) {UserId}")]
    public partial void OperatorReset(string outcome, Guid? userId);

    [LoggerMessage(EventId = 1009, EventName = "gozleme.audit.operator_unlock", Level = LogLevel.Warning, Message = "Operator unlock ({Outcome}) {UserId}")]
    public partial void OperatorUnlock(string outcome, Guid? userId);

    [LoggerMessage(EventId = 1010, EventName = "gozleme.audit.seeded", Level = LogLevel.Information, Message = "Demo account seeded {UserId}")]
    public partial void Seeded(Guid userId);
}
