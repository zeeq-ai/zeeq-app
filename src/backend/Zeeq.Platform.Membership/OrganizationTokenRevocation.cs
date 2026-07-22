using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Shared best-effort token-revocation policy used by both the admin-removal
/// (<see cref="RemoveMemberHandler"/>) and self-service-leave
/// (<see cref="LeaveOrgHandler"/>) membership handlers.
/// </summary>
/// <remarks>
/// Revokes the member's organization-scoped API tokens, but never fails the
/// membership mutation itself if the revoke fails — the cached
/// membership-status check in <c>UserTokenValidationMiddleware</c> is the
/// backstop if this revoke is lost. Request cancellation still propagates;
/// only revocation-specific failures are treated as non-fatal.
/// </remarks>
internal static partial class OrganizationTokenRevocation
{
    /// <summary>
    /// Revokes the user's organization-scoped tokens, logging success or
    /// failure via the caller's own <paramref name="logger"/> so the log
    /// category still identifies which handler triggered the revoke.
    /// </summary>
    public static async Task RevokeBestEffortAsync(
        IZeeqIdentityStore identityStore,
        string organizationId,
        string userId,
        ILogger logger,
        CancellationToken ct
    )
    {
        try
        {
            var revokedCount = await identityStore.RevokeUserTokensForOrganizationMemberAsync(
                organizationId,
                userId,
                DateTimeOffset.UtcNow,
                ct
            );
            LogTokensRevoked(logger, organizationId, userId, revokedCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve request-abort semantics — only revocation failures
            // are treated as non-fatal, not a canceled request.
            throw;
        }
        catch (Exception ex)
        {
            LogTokenRevocationFailed(logger, organizationId, userId, ex);
        }
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Revoked user tokens after membership change. OrganizationId={OrganizationId}, UserId={UserId}, RevokedCount={RevokedCount}"
    )]
    private static partial void LogTokensRevoked(
        ILogger logger,
        string organizationId,
        string userId,
        int revokedCount
    );

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "Token revocation failed after membership change. OrganizationId={OrganizationId}, UserId={UserId}"
    )]
    private static partial void LogTokenRevocationFailed(
        ILogger logger,
        string organizationId,
        string userId,
        Exception exception
    );
}
