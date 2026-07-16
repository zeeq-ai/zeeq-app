using System.Security.Claims;
using System.Text.Json;
using Zeeq.Core.Models;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Coordinates the local setup lifecycle for public clients created through DCR.
/// </summary>
/// <remarks>
/// DCR is anonymous so MCP clients can discover and register before a browser
/// session exists. The client is only usable after the first authorization
/// request is completed by a logged-in user from the configured external IdP.
/// </remarks>
public sealed partial class DcrClientSetupService(
    IZeeqIdentityStore identityStore,
    ILogger<DcrClientSetupService> log
)
{
    private static readonly TimeSpan PendingSetupLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Creates the local pending setup row for a newly registered public DCR client.
    /// </summary>
    /// <remarks>
    /// Pending rows are bounded retry artifacts. They let DCR-first clients register
    /// before a browser login exists, but they must expire and must not be treated as
    /// standalone authorized clients.
    /// </remarks>
    public async Task CreatePendingAsync(
        string clientId,
        string? clientName,
        IReadOnlyList<string> redirectUris,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken
    )
    {
        var createdAt = DateTimeOffset.UtcNow;
        var setup = new DcrClientSetup
        {
            ClientId = clientId,
            Status = DcrClientSetup.PendingLogin,
            ClientName = string.IsNullOrWhiteSpace(clientName) ? "MCP Client" : clientName,
            RedirectUrisJson = JsonSerializer.Serialize(redirectUris),
            RequestedScopes = string.Join(' ', scopes),
            CreatedAtUtc = createdAt,
            ExpiresAtUtc = createdAt.Add(PendingSetupLifetime),
        };

        await identityStore.CreatePendingDcrSetupAsync(setup, cancellationToken);

        LogPendingSetupCreated(clientId, setup.ExpiresAtUtc);
    }

    /// <summary>
    /// Claims a pending DCR setup for the authenticated user or validates an existing active setup.
    /// </summary>
    public async Task<DcrSetupDecision> ClaimOrValidateActiveAsync(
        string? clientId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The client_id parameter is required."
            );
        }

        var setup = await identityStore.FindDcrSetupAsync(clientId, cancellationToken);
        if (setup is null)
        {
            return RejectMissingSetupIfDcrClient(clientId);
        }

        var owner = GetOwner(user);
        if (owner is null)
        {
            return DcrSetupDecision.Reject(
                Errors.InvalidRequest,
                "The authenticated user is missing external identity claims."
            );
        }

        if (IsRevoked(setup))
        {
            LogSetupRejected(clientId, setup.Status, "revoked");
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The dynamically registered MCP client has been revoked."
            );
        }

        if (IsExpired(setup))
        {
            await MarkExpiredAsync(setup, cancellationToken);
            LogSetupRejected(clientId, setup.Status, "expired");
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The dynamically registered MCP client setup has expired."
            );
        }

        if (setup.Status == DcrClientSetup.Active)
        {
            return IsSameOwner(setup, owner)
                ? DcrSetupDecision.Allow()
                : DcrSetupDecision.Reject(
                    Errors.AccessDenied,
                    "The dynamically registered MCP client belongs to a different user."
                );
        }

        if (setup.Status != DcrClientSetup.PendingLogin)
        {
            LogSetupRejected(clientId, setup.Status, "unexpected status");
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The dynamically registered MCP client setup is not active."
            );
        }

        // /connect/authorize is the convergence point for both prototype flows:
        // DCR first then login, and login first then DCR. Claiming here guarantees
        // the authorization code is only issued after the local owner binding succeeds.
        await identityStore.ClaimDcrSetupAsync(
            clientId,
            new OwnerContext(
                owner.UserId,
                owner.OrganizationId,
                owner.TeamId,
                owner.PartitionIdsJson,
                owner.Provider,
                owner.ProviderSubject
            ),
            cancellationToken
        );

        LogSetupClaimed(clientId, owner.UserId, owner.Provider);
        return DcrSetupDecision.Allow();
    }

    /// <summary>
    /// Validates that a DCR client setup is active before token exchange.
    /// </summary>
    public async Task<DcrSetupDecision> ValidateActiveForTokenExchangeAsync(
        string? clientId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The client_id parameter is required."
            );
        }

        var setup = await identityStore.FindDcrSetupAsync(clientId, cancellationToken);
        if (setup is null)
        {
            return RejectMissingSetupIfDcrClient(clientId);
        }

        if (IsRevoked(setup))
        {
            LogSetupRejected(clientId, setup.Status, "revoked token exchange");
            return DcrSetupDecision.Reject(
                Errors.InvalidClient,
                "The dynamically registered MCP client has been revoked."
            );
        }

        if (setup.Status != DcrClientSetup.Active || string.IsNullOrWhiteSpace(setup.ClaimedUserId))
        {
            LogSetupRejected(clientId, setup.Status, "unclaimed token exchange");
            return DcrSetupDecision.Reject(
                Errors.InvalidGrant,
                "The dynamically registered MCP client setup has not been finalized."
            );
        }

        return DcrSetupDecision.Allow();
    }

    private async Task MarkExpiredAsync(DcrClientSetup setup, CancellationToken cancellationToken)
    {
        if (setup.Status == DcrClientSetup.PendingLogin)
        {
            await identityStore.MarkDcrSetupExpiredAsync(setup.ClientId, cancellationToken);
        }
    }

    private static bool IsRevoked(DcrClientSetup setup) =>
        setup.RevokedAtUtc is not null || setup.Status == DcrClientSetup.Revoked;

    private static bool IsExpired(DcrClientSetup setup) =>
        setup.Status == DcrClientSetup.PendingLogin && setup.ExpiresAtUtc <= DateTimeOffset.UtcNow;

    private static bool IsSameOwner(DcrClientSetup setup, OwnerIdentity owner) =>
        setup.ClaimedUserId == owner.UserId
        && setup.OrganizationId == owner.OrganizationId
        && setup.TeamId == owner.TeamId
        && setup.ClaimedOwnerProvider == owner.Provider
        && setup.ClaimedOwnerProviderSubject == owner.ProviderSubject;

    private static DcrSetupDecision RejectMissingSetupIfDcrClient(string clientId)
    {
        if (!IsDcrClientId(clientId))
        {
            return DcrSetupDecision.Allow();
        }

        return DcrSetupDecision.Reject(
            Errors.InvalidClient,
            "The dynamically registered MCP client is missing setup state."
        );
    }

    private static bool IsDcrClientId(string clientId) =>
        clientId.StartsWith("mcp_", StringComparison.Ordinal);

    /// <summary>
    /// Extracts the local owner identity from the cookie principal created after IdP login.
    /// </summary>
    /// <remarks>
    /// Owner fields must come from the authenticated local principal, not from the
    /// DCR or token request. This keeps DCR setup bound to the verified upstream
    /// identity that completed browser login.
    /// </remarks>
    private static OwnerIdentity? GetOwner(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(Claims.Subject);
        var organizationId = user.FindFirstValue(AuthClaims.OrganizationId);
        var teamId = user.FindFirstValue(AuthClaims.TeamId);
        var partitionIdsJson = user.FindFirstValue(AuthClaims.PartitionIds) ?? "[]";
        var provider = user.FindFirstValue(AuthClaims.Provider);
        var providerSubject = user.FindFirstValue(AuthClaims.ProviderSubject);

        return
            string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(organizationId)
            || string.IsNullOrWhiteSpace(teamId)
            || string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(providerSubject)
            ? null
            : new OwnerIdentity(
                userId,
                organizationId,
                teamId,
                partitionIdsJson,
                provider,
                providerSubject
            );
    }

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "Created pending DCR client setup. ClientId={ClientId}, ExpiresAtUtc={ExpiresAtUtc}"
    )]
    private partial void LogPendingSetupCreated(string clientId, DateTimeOffset expiresAtUtc);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "Claimed DCR client setup. ClientId={ClientId}, OwnerUserId={OwnerUserId}, OwnerProvider={OwnerProvider}"
    )]
    private partial void LogSetupClaimed(string clientId, string ownerUserId, string ownerProvider);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Warning,
        Message = "Rejected DCR client setup. ClientId={ClientId}, Status={Status}, Reason={Reason}"
    )]
    private partial void LogSetupRejected(string clientId, string status, string reason);

    private sealed record OwnerIdentity(
        string UserId,
        string OrganizationId,
        string TeamId,
        string PartitionIdsJson,
        string Provider,
        string ProviderSubject
    );
}

/// <summary>
/// Decision returned by DCR setup validation before OpenIddict continues the OAuth flow.
/// </summary>
/// <param name="Succeeded">Whether the client setup state allows the request.</param>
/// <param name="Error">OAuth error code to return when validation fails.</param>
/// <param name="ErrorDescription">Diagnostic error description for failed validation.</param>
public sealed record DcrSetupDecision(bool Succeeded, string? Error, string? ErrorDescription)
{
    /// <summary>
    /// Allows the OAuth request to continue.
    /// </summary>
    public static DcrSetupDecision Allow() => new(true, null, null);

    /// <summary>
    /// Rejects the OAuth request with an error and description.
    /// </summary>
    public static DcrSetupDecision Reject(string error, string description) =>
        new(false, error, description);
}
