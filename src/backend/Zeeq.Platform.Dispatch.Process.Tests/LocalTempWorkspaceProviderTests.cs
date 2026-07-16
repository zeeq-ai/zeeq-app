using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Tests for <see cref="LocalTempWorkspaceProvider"/> against a real local git
/// repo used as the clone source — no network dependency.
///
/// dotnet run --project src/backend/Zeeq.Platform.Dispatch.Process.Tests --output detailed --disable-logo --treenode-filter "/*/*/LocalTempWorkspaceProviderTests/*"
/// </summary>
public sealed class LocalTempWorkspaceProviderTests
{
    private static AppSettings SettingsWithRoot(string root) =>
        new() { Ingest = new() { ContentRootPath = root } };

    private static RepositoryIngestJob PublicJob(string repoUrl) =>
        new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Public,
            RepoUrl = repoUrl,
            PublicSourceId = "src_1",
            Trigger = IngestTriggerReason.Manual,
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static RepositoryIngestJob PrivateJob(
        string repoUrl,
        string organizationId = "org_1",
        string libraryId = "library_1"
    ) =>
        new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Private,
            RepoUrl = repoUrl,
            OrganizationId = organizationId,
            LibraryId = libraryId,
            Trigger = IngestTriggerReason.Manual,
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static string NewContentRoot() =>
        Path.Combine(Path.GetTempPath(), $"zeeq-workspace-test-{Guid.NewGuid():N}");

    private static LocalTempWorkspaceProvider NewProvider(string contentRoot)
    {
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        return new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );
    }

    [Test]
    public async Task AcquireAsync_FreshClone_SparseChecksOutOnlyMarkdown()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");
        remote.Commit("notes.txt", "not markdown");

        var contentRoot = NewContentRoot();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );

        await using var workspace = await provider.AcquireAsync(
            PublicJob(remote.Path),
            CancellationToken.None
        );

        await Assert.That(File.Exists(Path.Combine(workspace.RootPath, "guide.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(workspace.RootPath, "notes.txt"))).IsFalse();

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_DeterministicPath_MatchesSpecScheme()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );

        await using var workspace = await provider.AcquireAsync(
            PublicJob(remote.Path),
            CancellationToken.None
        );

        await Assert.That(workspace.RootPath).StartsWith(Path.Combine(contentRoot, "public"));

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_PrivateJob_PathDoesNotContainOrgOrLibraryIdAsPlainSegments()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var provider = NewProvider(contentRoot);

        await using var workspace = await provider.AcquireAsync(
            PrivateJob(remote.Path, organizationId: "org_secret", libraryId: "library_secret"),
            CancellationToken.None
        );

        // Obfuscation: a private workspace path must not leak the plain-text
        // org/library id as a directory segment — anyone who can `list` the
        // shared mounted bucket in production should not be able to group
        // entries by organization just by walking the tree.
        await Assert.That(workspace.RootPath).StartsWith(Path.Combine(contentRoot, "private"));
        await Assert.That(workspace.RootPath).DoesNotContain("org_secret");
        await Assert.That(workspace.RootPath).DoesNotContain("library_secret");

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_PrivateJob_DifferentOrgsSameRepo_ResolveToDifferentPaths()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var provider = NewProvider(contentRoot);

        await using var workspaceA = await provider.AcquireAsync(
            PrivateJob(remote.Path, organizationId: "org_a", libraryId: "library_1"),
            CancellationToken.None
        );
        await using var workspaceB = await provider.AcquireAsync(
            PrivateJob(remote.Path, organizationId: "org_b", libraryId: "library_1"),
            CancellationToken.None
        );

        await Assert.That(workspaceA.RootPath).IsNotEqualTo(workspaceB.RootPath);

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_PrivateJob_SameInputsTwice_ResolveToSamePath()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var provider = NewProvider(contentRoot);
        var job = PrivateJob(remote.Path, organizationId: "org_a", libraryId: "library_1");

        await using var first = await provider.AcquireAsync(job, CancellationToken.None);
        var firstPath = first.RootPath;
        await first.DisposeAsync();

        await using var second = await provider.AcquireAsync(job, CancellationToken.None);

        await Assert.That(second.RootPath).IsEqualTo(firstPath);

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_ReAcquireAfterNewCommit_PullsLatestChange()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# v1");

        var contentRoot = NewContentRoot();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );

        var job = PublicJob(remote.Path);

        // First acquire — do NOT dispose, so the directory survives for the
        // pull-path check on the second acquire (dispose deletes it, per
        // LocalIngestWorkspace's delete-on-dispose semantics).
        var firstWorkspace = await provider.AcquireAsync(job, CancellationToken.None);
        await Assert
            .That(await File.ReadAllTextAsync(Path.Combine(firstWorkspace.RootPath, "guide.md")))
            .IsEqualTo("# v1");

        remote.Commit("guide.md", "# v2");

        await using var secondWorkspace = await provider.AcquireAsync(job, CancellationToken.None);
        await Assert
            .That(await File.ReadAllTextAsync(Path.Combine(secondWorkspace.RootPath, "guide.md")))
            .IsEqualTo("# v2");

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task AcquireAsync_CorruptExistingDirectory_ReClonesInstead()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );

        var job = PublicJob(remote.Path);

        // Pre-seed a non-git directory at the deterministic path — simulates a
        // half-written or corrupt prior workspace.
        var expectedPath = Path.Combine(
            contentRoot,
            "public",
            Convert
                .ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(remote.Path)
                    )
                )
                .ToLowerInvariant()[..16]
        );
        Directory.CreateDirectory(expectedPath);
        await File.WriteAllTextAsync(Path.Combine(expectedPath, "garbage.txt"), "not a git repo");

        await using var workspace = await provider.AcquireAsync(job, CancellationToken.None);

        await Assert.That(File.Exists(Path.Combine(workspace.RootPath, "guide.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(workspace.RootPath, "garbage.txt"))).IsFalse();

        Directory.Delete(contentRoot, recursive: true);
    }

    [Test]
    public async Task DisposeAsync_DeletesWorkspaceDirectory()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var provider = new LocalTempWorkspaceProvider(
            SettingsWithRoot(contentRoot),
            tokenProvider,
            new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
            NullLogger<LocalTempWorkspaceProvider>.Instance
        );

        var workspace = await provider.AcquireAsync(PublicJob(remote.Path), CancellationToken.None);
        var rootPath = workspace.RootPath;
        await Assert.That(Directory.Exists(rootPath)).IsTrue();

        await workspace.DisposeAsync();

        await Assert.That(Directory.Exists(rootPath)).IsFalse();

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
