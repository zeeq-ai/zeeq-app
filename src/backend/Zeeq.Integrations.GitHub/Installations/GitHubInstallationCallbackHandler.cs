using Zeeq.Core.Common;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Completes the GitHub App installation callback.
/// </summary>
public sealed partial class GitHubInstallationCallbackHandler(
    GitHubInstallationStateTokenProtector stateProtector,
    IGitHubInstallationVerifier verifier,
    IGitHubInstallationStore store,
    HttpSettings httpSettings,
    ILogger<GitHubInstallationCallbackHandler> logger
) : IEndpointHandler
{
    /// <summary>
    /// Verifies the GitHub installation id and links it to the organization in the state token.
    /// </summary>
    /// <remarks>
    /// This is the second step in the GitHub App install flow. GitHub calls this
    /// handler after an operator installs or updates the app. The callback is
    /// anonymous because it may arrive through a devtunnel or public host where
    /// the local Zeeq auth cookie is not present.
    ///
    /// The handler first opens the state token created by
    /// <see cref="GitHubInstallationLinkHandler"/> using
    /// <see cref="GitHubInstallationStateTokenProtector"/>. That state says
    /// which Zeeq organization started the install. It then asks
    /// <see cref="IGitHubInstallationVerifier"/> to verify the GitHub
    /// installation id with GitHub App authentication. After verification, it
    /// stores the canonical local link as a <see cref="GitHubAppInstallation"/>
    /// through <see cref="IGitHubInstallationStore"/>.
    ///
    /// If the same GitHub installation is already linked to a different Zeeq
    /// organization, the store raises
    /// <see cref="GitHubInstallationLinkConflictException"/> and this handler
    /// returns a bad request instead of pretending the link succeeded.
    /// </remarks>
    public async Task<IResult> HandleAsync(
        long? installationId,
        string? setupAction,
        string? state,
        CancellationToken cancellationToken
    )
    {
        if (installationId is null)
        {
            return Results.BadRequest(
                new GitHubInstallationError("The installation_id query parameter is required.")
            );
        }

        if (!stateProtector.TryUnprotect(state, out var payload) || payload is null)
        {
            return Results.BadRequest(
                new GitHubInstallationError("The state parameter is invalid or expired.")
            );
        }

        GitHubInstallationVerification verification;

        try
        {
            verification = await verifier.VerifyAsync(installationId.Value, cancellationToken);
        }
        catch (ApiException ex)
        {
            LogVerificationFailure(logger, installationId.Value, ex.StatusCode.ToString());

            return Results.BadRequest(
                new GitHubInstallationError("GitHub could not verify the installation.")
            );
        }

        var now = DateTimeOffset.UtcNow;

        // PHASE ONE TRUST MODEL: this links a GitHub-verified installation id to
        // the Zeeq organization carried in the short-lived state token that an
        // authenticated org admin generated. This does not yet prove that the
        // Zeeq user owns or controls the installed GitHub account through a
        // GitHub user access token. Full GitHub account/ownership verification is
        // intentionally deferred to the next account-linking slice.
        try
        {
            await store.UpsertLinkedInstallationAsync(
                new GitHubAppInstallation
                {
                    Id = "ghi_" + Guid.CreateVersion7().ToString("N"),
                    OrganizationId = payload.OrganizationId,
                    TeamId = payload.TeamId,
                    InstallationId = verification.InstallationId,
                    AccountLogin = verification.AccountLogin,
                    AccountId = verification.AccountId,
                    AccountType = verification.AccountType,
                    RepositorySelection = verification.RepositorySelection,
                    InstalledAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SuspendedAtUtc = verification.SuspendedAtUtc,
                    DeletedAtUtc = null,
                    RawInstallationJson = verification.RawInstallationJson,
                },
                cancellationToken
            );
        }
        catch (GitHubInstallationLinkConflictException ex)
        {
            LogLinkConflict(
                logger,
                ex.InstallationId,
                ex.ExistingOrganizationId,
                ex.RequestedOrganizationId
            );

            return Results.BadRequest(
                new GitHubInstallationError(
                    "This GitHub App installation is already linked to a different Zeeq organization."
                )
            );
        }

        LogLinkedInstallation(
            logger,
            payload.OrganizationId,
            verification.InstallationId,
            verification.AccountLogin,
            setupAction ?? string.Empty
        );

        var frontendBaseUri = httpSettings.FrontendBaseUri.TrimEnd('/');

        return Results.Redirect($"{frontendBaseUri}/settings/github?installationLinked=true");
    }

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Warning,
        Message = "GitHub installation verification failed. InstallationId={InstallationId}, Status={Status}"
    )]
    private static partial void LogVerificationFailure(
        ILogger logger,
        long installationId,
        string status
    );

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Rejected GitHub App installation relink. InstallationId={InstallationId}, ExistingOrganizationId={ExistingOrganizationId}, RequestedOrganizationId={RequestedOrganizationId}"
    )]
    private static partial void LogLinkConflict(
        ILogger logger,
        long installationId,
        string existingOrganizationId,
        string requestedOrganizationId
    );

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Linked GitHub App installation. OrganizationId={OrganizationId}, InstallationId={InstallationId}, AccountLogin={AccountLogin}, SetupAction={SetupAction}"
    )]
    private static partial void LogLinkedInstallation(
        ILogger logger,
        string organizationId,
        long installationId,
        string accountLogin,
        string setupAction
    );
}
