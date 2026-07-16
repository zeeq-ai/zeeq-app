using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="IngestGitHubTokenProvider"/> token resolution chain.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/IngestGitHubTokenProviderTests/*"
/// </summary>
[NotInParallel]
public sealed class IngestGitHubTokenProviderTests
{
    private static RepositoryIngestJob PrivateJob(long? installationId) =>
        new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Private,
            RepoUrl = "https://github.com/acme/docs",
            Trigger = IngestTriggerReason.Manual,
            OrganizationId = "org_1",
            LibraryId = "lib_1",
            InstallationId = installationId,
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static RepositoryIngestJob PublicJob() =>
        new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/acme/docs",
            Trigger = IngestTriggerReason.Scheduled,
            PublicSourceId = "src_1",
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    [Test]
    public async Task GetTokenAsync_AlwaysUseGhTokenForSync_ReturnsEnvVarEvenWithInstallation()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "forced-pat-value");
        try
        {
            var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
            var provider = new IngestGitHubTokenProvider(
                new AppSettings { GitHub = new() { AlwaysUseGhTokenForSync = true } },
                installationTokenProvider,
                NullLogger<IngestGitHubTokenProvider>.Instance
            );

            var token = await provider.GetTokenAsync(PrivateJob(42), CancellationToken.None);

            await Assert.That(token).IsEqualTo("forced-pat-value");
            await installationTokenProvider
                .DidNotReceive()
                .GetInstallationTokenAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    [Test]
    public async Task GetTokenAsync_PrivateJobWithInstallation_UsesInstallationToken()
    {
        var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
        installationTokenProvider
            .GetInstallationTokenAsync(42, Arg.Any<CancellationToken>())
            .Returns("installation-token-value");

        var provider = new IngestGitHubTokenProvider(
            new AppSettings(),
            installationTokenProvider,
            NullLogger<IngestGitHubTokenProvider>.Instance
        );

        var token = await provider.GetTokenAsync(PrivateJob(42), CancellationToken.None);

        await Assert.That(token).IsEqualTo("installation-token-value");
    }

    [Test]
    public async Task GetTokenAsync_InstallationTokenFails_FallsBackToGhToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "fallback-pat-value");
        try
        {
            var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
            installationTokenProvider
                .GetInstallationTokenAsync(42, Arg.Any<CancellationToken>())
                .Returns<string>(_ => throw new InvalidOperationException("mint failed"));

            var provider = new IngestGitHubTokenProvider(
                new AppSettings(),
                installationTokenProvider,
                NullLogger<IngestGitHubTokenProvider>.Instance
            );

            var token = await provider.GetTokenAsync(PrivateJob(42), CancellationToken.None);

            await Assert.That(token).IsEqualTo("fallback-pat-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    [Test]
    public async Task GetTokenAsync_PublicJobNoInstallation_FallsBackToGhToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "public-pat-value");
        try
        {
            var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
            var provider = new IngestGitHubTokenProvider(
                new AppSettings(),
                installationTokenProvider,
                NullLogger<IngestGitHubTokenProvider>.Instance
            );

            var token = await provider.GetTokenAsync(PublicJob(), CancellationToken.None);

            await Assert.That(token).IsEqualTo("public-pat-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    [Test]
    public async Task GetTokenAsync_PublicJobNoTokenConfigured_ReturnsNullForAnonymousClone()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);

        var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
        var provider = new IngestGitHubTokenProvider(
            new AppSettings(),
            installationTokenProvider,
            NullLogger<IngestGitHubTokenProvider>.Instance
        );

        var token = await provider.GetTokenAsync(PublicJob(), CancellationToken.None);

        await Assert.That(token).IsNull();
    }

    [Test]
    public async Task GetTokenAsync_PrivateJobNoTokenAvailable_ThrowsActionableException()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", null);

        var installationTokenProvider = Substitute.For<IGitHubInstallationTokenProvider>();
        var provider = new IngestGitHubTokenProvider(
            new AppSettings(),
            installationTokenProvider,
            NullLogger<IngestGitHubTokenProvider>.Instance
        );

        await Assert
            .That(async () =>
                await provider.GetTokenAsync(
                    PrivateJob(installationId: null),
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>();
    }
}
