using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Models;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// Tests for the GitHub App installation client factory.
///
/// dotnet run --project src/backend/Zeeq.Integrations.GitHub.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubClientFactoryTests/*"
/// </summary>
public sealed class GitHubClientFactoryTests
{
    [Test]
    public async Task CreateInstallationClientForOrganizationAsync_UsesStoreInstallationAndCachesToken()
    {
        var tokenClient = new FakeInstallationTokenClient();
        var factory = CreateFactory(
            new FakeGitHubInstallationStore([
                CreateInstallation(organizationId: "org_1", installationId: 141782517),
            ]),
            tokenClient
        );

        var first = await factory.CreateInstallationClientForOrganizationAsync(
            "org_1",
            CancellationToken.None
        );
        var second = await factory.CreateInstallationClientForOrganizationAsync(
            "org_1",
            CancellationToken.None
        );

        await Assert.That(first.Credentials).IsNotNull();
        await Assert.That(second.Credentials).IsNotNull();
        await Assert.That(tokenClient.RequestedInstallationIds).IsEquivalentTo([141782517L]);
    }

    [Test]
    public async Task CreateInstallationClientForOrganizationAsync_WhenInstallationMissing_Throws()
    {
        var tokenClient = new FakeInstallationTokenClient();
        var factory = CreateFactory(new FakeGitHubInstallationStore([]), tokenClient);

        await Assert
            .That(async () =>
                await factory.CreateInstallationClientForOrganizationAsync(
                    "org_missing",
                    CancellationToken.None
                )
            )
            .Throws<GitHubInstallationUnavailableException>();
        await Assert.That(tokenClient.RequestedInstallationIds).IsEmpty();
    }

    [Test]
    public async Task CreateInstallationClientForOrganizationAsync_CachesByInstallationId()
    {
        var tokenClient = new FakeInstallationTokenClient();
        var factory = CreateFactory(
            new FakeGitHubInstallationStore([
                CreateInstallation(organizationId: "org_a", installationId: 100),
                CreateInstallation(organizationId: "org_b", installationId: 200),
            ]),
            tokenClient
        );

        await factory.CreateInstallationClientForOrganizationAsync("org_a", CancellationToken.None);
        await factory.CreateInstallationClientForOrganizationAsync("org_b", CancellationToken.None);
        await factory.CreateInstallationClientForOrganizationAsync("org_a", CancellationToken.None);

        await Assert.That(tokenClient.RequestedInstallationIds).IsEquivalentTo([100L, 200L]);
    }

    [Test]
    public async Task InstallationTokenCacheTtl_IsShorterThanGitHubTokenLifetime()
    {
        await Assert
            .That(OctokitGitHubClientFactory.InstallationTokenCacheTtl)
            .IsLessThan(TimeSpan.FromHours(1));
        await Assert
            .That(OctokitGitHubClientFactory.InstallationTokenCacheTtl)
            .IsEqualTo(TimeSpan.FromMinutes(55));
        await Assert
            .That(OctokitGitHubClientFactory.InstallationTokenLocalCacheTtl)
            .IsEqualTo(TimeSpan.FromMinutes(5));
    }

    private static OctokitGitHubClientFactory CreateFactory(
        IGitHubInstallationStore store,
        IGitHubInstallationTokenClient tokenClient
    )
    {
        var services = new ServiceCollection();
        services.AddHybridCache();

        var serviceProvider = services.BuildServiceProvider();

        return new(
            store,
            tokenClient,
            serviceProvider.GetRequiredService<HybridCache>(),
            NullLogger<OctokitGitHubClientFactory>.Instance
        );
    }

    private static GitHubAppInstallation CreateInstallation(
        string organizationId,
        long installationId
    ) =>
        new()
        {
            Id = $"ghi_{installationId}",
            OrganizationId = organizationId,
            InstallationId = installationId,
            AccountLogin = "zeeq-ai",
            AccountId = installationId,
            AccountType = "Organization",
            RepositorySelection = "all",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class FakeInstallationTokenClient : IGitHubInstallationTokenClient
    {
        public List<long> RequestedInstallationIds { get; } = [];

        public Task<string> CreateInstallationTokenAsync(
            long installationId,
            CancellationToken cancellationToken
        )
        {
            RequestedInstallationIds.Add(installationId);

            return Task.FromResult($"token-{installationId}");
        }
    }

    private sealed class FakeGitHubInstallationStore(
        IReadOnlyList<GitHubAppInstallation> installations
    ) : IGitHubInstallationStore
    {
        public Task<GitHubAppInstallation?> FindByInstallationIdAsync(
            long installationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                installations.SingleOrDefault(installation =>
                    installation.InstallationId == installationId
                )
            );

        public Task<GitHubAppInstallation?> FindActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                installations.SingleOrDefault(installation =>
                    installation.OrganizationId == organizationId
                )
            );

        public Task<GitHubAppInstallation> UpsertLinkedInstallationAsync(
            GitHubAppInstallation installation,
            CancellationToken cancellationToken
        ) => Task.FromResult(installation);

        public Task ApplyLifecycleEventAsync(
            long installationId,
            string repositorySelection,
            DateTimeOffset? suspendedAtUtc,
            DateTimeOffset? deletedAtUtc,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
