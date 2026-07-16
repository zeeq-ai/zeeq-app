using Microsoft.Extensions.Logging;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Rejects authorization-code exchanges for DCR clients that have not completed setup.
/// </summary>
/// <remarks>
/// OpenIddict validates the public client and PKCE values, then this handler
/// checks the local setup row. Pending DCR rows must fail token exchange with
/// <c>invalid_grant</c> until <c>/connect/authorize</c> claims them for a
/// logged-in local user.
/// </remarks>
public sealed partial class DcrTokenRequestValidator(
    DcrClientSetupService setupService,
    ILogger<DcrTokenRequestValidator> log
) : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenRequestContext>
{
    /// <summary>
    /// Runs during token request validation for authorization-code exchanges.
    /// </summary>
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        if (
            !string.Equals(
                context.Request.GrantType,
                GrantTypes.AuthorizationCode,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        var decision = await setupService.ValidateActiveForTokenExchangeAsync(
            context.Request.ClientId,
            context.CancellationToken
        );
        if (decision.Succeeded)
        {
            return;
        }

        LogTokenRequestRejected(
            context.Request.ClientId ?? "(missing)",
            decision.ErrorDescription ?? string.Empty
        );
        context.Reject(
            error: decision.Error ?? Errors.InvalidClient,
            description: decision.ErrorDescription
        );
    }

    [LoggerMessage(
        EventId = 1450,
        Level = LogLevel.Warning,
        Message = "DCR token request rejected. ClientId={ClientId}, Reason={Reason}"
    )]
    private partial void LogTokenRequestRejected(string clientId, string reason);
}
