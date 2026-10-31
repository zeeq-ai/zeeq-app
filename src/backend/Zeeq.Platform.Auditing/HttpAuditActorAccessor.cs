using Zeeq.Core.Auditing;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Auditing;

/// <summary>
/// Resolves the audit actor from the authenticated request principal, or from the explicit
/// scoped <see cref="AuditActorContext"/> when there is no authenticated HTTP caller.
/// </summary>
/// <remarks>
/// Resolution is lazy: the principal is only final after endpoint authorization has run, which is
/// before any store calls <see cref="Current"/>. Identity facts come from server-issued claims only.
/// For client credentials the OAuth <c>sub</c> is the client id and <c>owner_user_id</c> is the human
/// owner; for user tokens <c>sub</c> is the human. System-admin kind is used only when the matched
/// route is a system-admin route and the live evaluator confirms the authority.
/// </remarks>
internal sealed class HttpAuditActorAccessor(
    IHttpContextAccessor httpContextAccessor,
    AuditActorContext explicitContext,
    SystemAdminEvaluator systemAdmin
) : IAuditActorAccessor
{
    private AuditActor? _resolved;

    /// <inheritdoc />
    public AuditActor Current =>
        _resolved ??= httpContextAccessor.HttpContext
            is { User.Identity.IsAuthenticated: true } http
            ? Resolve(http)
            : explicitContext.Current;

    /// <inheritdoc />
    public Guid OperationId => explicitContext.OperationId;

    /// <inheritdoc />
    public string? RequestId =>
        httpContextAccessor.HttpContext?.TraceIdentifier ?? explicitContext.RequestId;

    private AuditActor Resolve(HttpContext http)
    {
        var principal = http.User;
        var identity = principal.AsZeeqIdentity();
        var profile = principal.AuthenticatedUser();
        var authMode = principal.FindFirstValue(AuthClaims.AuthMode);
        var credentialId = principal.FindFirstValue(AuthClaims.ClientCredentialId);
        var tokenId = principal.FindFirstValue(AuthClaims.UserTokenId);
        var isMcp = http.Request.Path.StartsWithSegments("/mcp");
        var isSystemConsole =
            http.GetEndpoint()?.Metadata.GetMetadata<HiddenAdminRouteMetadata>() is not null
            && systemAdmin.IsSystemAdmin(principal);

        if (credentialId is not null)
        {
            // Machine credential: the actor is the credential; the human owner is separate.
            var credentialName = principal.FindFirstValue(AuthClaims.ClientCredentialName);

            return new AuditActor(
                Kind: AuditActorKinds.ClientCredential,
                Id: credentialId,
                DisplayName: credentialName ?? credentialId,
                Channel: isMcp ? AuditChannels.Mcp : AuditChannels.Api,
                ResponsibleUserId: principal.FindFirstValue(AuthClaims.OwnerUserId),
                CredentialId: credentialId,
                CredentialName: credentialName
            );
        }

        var userId = identity.OwnerUserId;
        var displayName = profile?.Name ?? profile?.Email ?? userId;

        if (tokenId is not null)
        {
            return new AuditActor(
                Kind: AuditActorKinds.UserToken,
                Id: userId,
                DisplayName: displayName,
                Channel: isMcp ? AuditChannels.Mcp : AuditChannels.Api,
                ResponsibleUserId: userId,
                CredentialId: tokenId,
                CredentialName: principal.FindFirstValue(AuthClaims.UserTokenName)
            );
        }

        return new AuditActor(
            Kind: isSystemConsole ? AuditActorKinds.SystemAdmin : AuditActorKinds.User,
            Id: userId,
            DisplayName: displayName,
            Channel: isSystemConsole ? AuditChannels.SystemConsole
                : authMode is not null || isMcp ? (isMcp ? AuditChannels.Mcp : AuditChannels.Api)
                : AuditChannels.Web,
            ResponsibleUserId: userId
        );
    }
}
