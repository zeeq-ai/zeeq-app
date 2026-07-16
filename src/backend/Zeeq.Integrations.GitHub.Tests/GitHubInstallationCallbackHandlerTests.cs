using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Integrations.GitHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubInstallationCallbackHandlerTests
{
    [Test]
    public async Task HandleAsync_WithExistingInstallationLinkedToDifferentOrg_ReturnsBadRequest()
    {
        var protector = CreateProtector();
        var state = protector.Protect(
            new GitHubInstallationStatePayload(
                OrganizationId: "org_requested",
                TeamId: "team_requested",
                UserId: "user_123",
                Nonce: "nonce",
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5)
            )
        );
        var handler = new GitHubInstallationCallbackHandler(
            protector,
            new TestGitHubInstallationVerifier(),
            new ConflictingGitHubInstallationStore(),
            new HttpSettings(),
            NullLogger<GitHubInstallationCallbackHandler>.Instance
        );

        var result = await handler.HandleAsync(12345, "install", state, CancellationToken.None);

        await Assert.That(result).IsTypeOf<BadRequest<GitHubInstallationError>>();
    }

    private static GitHubInstallationStateTokenProtector CreateProtector() =>
        new(new GitHubSettings { PrivateKeyPem = "test-state-protection-secret" });

    private sealed class TestGitHubInstallationVerifier : IGitHubInstallationVerifier
    {
        public Task<GitHubInstallationVerification> VerifyAsync(
            long installationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new GitHubInstallationVerification(
                    InstallationId: installationId,
                    AccountLogin: "octo-org",
                    AccountId: 98765,
                    AccountType: "Organization",
                    RepositorySelection: "selected",
                    SuspendedAtUtc: null,
                    RawInstallationJson: "{}"
                )
            );
    }

    private sealed class ConflictingGitHubInstallationStore : IGitHubInstallationStore
    {
        public Task<GitHubAppInstallation?> FindByInstallationIdAsync(
            long installationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<GitHubAppInstallation?>(null);

        public Task<GitHubAppInstallation?> FindActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<GitHubAppInstallation?>(null);

        public Task<GitHubAppInstallation> UpsertLinkedInstallationAsync(
            GitHubAppInstallation installation,
            CancellationToken cancellationToken
        ) =>
            throw new GitHubInstallationLinkConflictException(
                installation.InstallationId,
                "org_existing",
                "team_existing",
                installation.OrganizationId,
                installation.TeamId
            );

        public Task ApplyLifecycleEventAsync(
            long installationId,
            string repositorySelection,
            DateTimeOffset? suspendedAtUtc,
            DateTimeOffset? deletedAtUtc,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
