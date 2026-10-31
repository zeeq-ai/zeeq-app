namespace Zeeq.Core.Auditing;

/// <summary>Stable <see cref="AuditActor.Kind"/> values.</summary>
public static class AuditActorKinds
{
    public const string User = "user";
    public const string UserToken = "user_token";
    public const string ClientCredential = "client_credential";

    /// <summary>Only when system-console authority was actually exercised (admin route plus live evaluator).</summary>
    public const string SystemAdmin = "system_admin";
    public const string System = "system";
    public const string Webhook = "webhook";
    public const string Deployment = "deployment";
}

/// <summary>Stable <see cref="AuditActor.Channel"/> values; webhook channels are <c>webhook:{provider}</c>.</summary>
public static class AuditChannels
{
    public const string Web = "web";
    public const string Api = "api";
    public const string Mcp = "mcp";
    public const string Worker = "worker";
    public const string SystemConsole = "system_console";
    public const string Deployment = "deployment";
}

/// <summary>Honest description of what the recorded history covers during phased rollout.</summary>
public static class AuditCoverage
{
    /// <summary>Returned in every page until broader capture is inventoried and verified.</summary>
    public const string FirstSlice =
        "partial: local document changes, organization administration, membership and activation keys; other areas are not yet recorded";
}

/// <summary>Bounds applied to audit writes and reads.</summary>
public static class AuditLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxSnapshotCharacters = 16 * 1024 * 1024;
    public const int MaxChangedFields = 64;
    public const int MaxLabelLength = 512;
    public const int MaxIdentifierLength = 256;
}
