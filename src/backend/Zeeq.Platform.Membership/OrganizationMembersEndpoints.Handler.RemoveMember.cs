using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Disables a membership without deleting the row — preserves audit
/// history. Verifies the target user is actually a member first.
/// </summary>
public sealed partial class RemoveMemberHandler(
    IZeeqMembershipStore store,
    IZeeqIdentityStore identityStore,
    ILogger<RemoveMemberHandler> logger
) : IEndpointHandler
{
    /// <summary>
    /// Soft-deletes a membership after verifying the target user is a
    /// member. Preserves audit history. Also revokes the removed member's
    /// organization-scoped API tokens, best-effort — see
    /// <c>.agents/plans/2026-07-22-revoke-user-tokens-on-member-removal.spec.md</c>.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string orgId,
        string userId,
        CancellationToken ct
    )
    {
        // Verify the target user is a member of this org
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
        {
            return TypedResults.NotFound();
        }

        // Soft-delete: Status → Disabled, record timestamp, clear default flag
        await store.RemoveMemberAsync(orgId, userId, ct);

        try
        {
            var revokedCount = await identityStore.RevokeUserTokensForOrganizationMemberAsync(
                orgId,
                userId,
                DateTimeOffset.UtcNow,
                ct
            );
            LogTokensRevoked(logger, orgId, userId, revokedCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve request-abort semantics — only revocation failures
            // are treated as non-fatal, not a canceled request.
            throw;
        }
        catch (Exception ex)
        {
            // Non-fatal: membership removal already succeeded. The cached
            // membership-status check in UserTokenValidationMiddleware is
            // the backstop if this revoke is lost.
            LogTokenRevocationFailed(logger, orgId, userId, ex);
        }

        return TypedResults.NoContent();
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Revoked user tokens after member removal. OrganizationId={OrganizationId}, UserId={UserId}, RevokedCount={RevokedCount}"
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
        Message = "Token revocation failed after member removal. OrganizationId={OrganizationId}, UserId={UserId}"
    )]
    private static partial void LogTokenRevocationFailed(
        ILogger logger,
        string organizationId,
        string userId,
        Exception exception
    );
}
