using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Platform.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Tests for <see cref="InProcessIngestDispatcher"/> — the composition of
/// workspace acquisition, the runner, and disposal, plus the run-record
/// coverage this dispatcher adds for failures the runner never sees (workspace
/// acquisition, and anything the runner throws before creating its own record).
///
/// dotnet run --project src/backend/Zeeq.Platform.Dispatch.Process.Tests --output detailed --disable-logo --treenode-filter "/*/*/InProcessIngestDispatcherTests/*"
/// </summary>
public sealed class InProcessIngestDispatcherTests
{
    private static RepositoryIngestJob PublicJob(string repoUrl, string? runId = null) =>
        new()
        {
            RunId = runId ?? $"run_{Guid.NewGuid():N}",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Public,
            RepoUrl = repoUrl,
            PublicSourceId = "src_1",
            Trigger = IngestTriggerReason.Manual,
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static string NewContentRoot() =>
        Path.Combine(Path.GetTempPath(), $"zeeq-dispatcher-test-{Guid.NewGuid():N}");

    [Test]
    public async Task RunAsync_SuccessfulClone_ReturnsCompletedAndRecordsSucceededRun()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dispatcher = new InProcessIngestDispatcher(
            new LocalTempWorkspaceProvider(
                new AppSettings { Ingest = new() { ContentRootPath = contentRoot } },
                tokenProvider,
                new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
                NullLogger<LocalTempWorkspaceProvider>.Instance
            ),
            new RepositoryIngestRunner(
                documentStore,
                new FakeLibraryDocumentStore(),
                runStore,
                NullLogger<RepositoryIngestRunner>.Instance
            ),
            runStore,
            NullLogger<InProcessIngestDispatcher>.Instance
        );

        var job = PublicJob(remote.Path);
        var outcome = await dispatcher.RunAsync(job, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(DispatchOutcomeKind.Completed);

        var run = await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, CancellationToken.None);
        await Assert.That(run!.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(documentStore.Documents.Single().Path).IsEqualTo("/guide.md");

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_AcquireFails_ReturnsFailedAndRecordsFailedRun()
    {
        var contentRoot = NewContentRoot();
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dispatcher = new InProcessIngestDispatcher(
            new LocalTempWorkspaceProvider(
                new AppSettings { Ingest = new() { ContentRootPath = contentRoot } },
                tokenProvider,
                new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
                NullLogger<LocalTempWorkspaceProvider>.Instance
            ),
            new RepositoryIngestRunner(
                documentStore,
                new FakeLibraryDocumentStore(),
                runStore,
                NullLogger<RepositoryIngestRunner>.Instance
            ),
            runStore,
            NullLogger<InProcessIngestDispatcher>.Instance
        );

        // Not a real git repo, and not a git-cloneable path — clone-or-pull
        // exhausts its 2 attempts and AcquireAsync throws.
        var job = PublicJob(
            repoUrl: Path.Combine(Path.GetTempPath(), $"nonexistent-repo-{Guid.NewGuid():N}")
        );

        var outcome = await dispatcher.RunAsync(job, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(DispatchOutcomeKind.Failed);
        await Assert.That(outcome.FailureReason).IsNotNull();

        // This is the core behavior this dispatcher adds: even though the
        // runner was never invoked (acquisition failed first), a run record
        // still exists and is marked Failed — no silent audit-trail gap.
        var run = await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, CancellationToken.None);
        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Status).IsEqualTo(IngestRunStatus.Failed);
        await Assert.That(run.FailureMessage).IsNotNull();

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_RunRecordAlreadyExists_FallsBackToFinalizeOnly()
    {
        // RecordFatalFailureAsync has two branches: create-then-finalize (no
        // record exists yet — covered by the two tests above, since neither
        // the runner nor the dispatcher had created one before the failure)
        // and finalize-only (a record already exists, so CreateAsync throws
        // and the fallback finalizes it instead). This test pre-seeds a
        // record at the job's exact (RunId, CreatedAtUtc) — simulating the
        // runner having successfully created it before some later step
        // failed — to exercise that second branch directly.
        var contentRoot = NewContentRoot();
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dispatcher = new InProcessIngestDispatcher(
            new LocalTempWorkspaceProvider(
                new AppSettings { Ingest = new() { ContentRootPath = contentRoot } },
                tokenProvider,
                new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
                NullLogger<LocalTempWorkspaceProvider>.Instance
            ),
            new RepositoryIngestRunner(
                documentStore,
                new FakeLibraryDocumentStore(),
                runStore,
                NullLogger<RepositoryIngestRunner>.Instance
            ),
            runStore,
            NullLogger<InProcessIngestDispatcher>.Instance
        );

        var job = PublicJob(
            repoUrl: Path.Combine(Path.GetTempPath(), $"nonexistent-repo-{Guid.NewGuid():N}")
        );

        var preExisting = new DocsIngestRun
        {
            Id = job.RunId,
            CreatedAtUtc = job.RunCreatedAtUtc,
            SourceKind = job.Kind,
            RepoUrl = job.RepoUrl,
            PublicSourceId = job.PublicSourceId,
            Trigger = job.Trigger,
            Status = IngestRunStatus.Running,
            StartedAtUtc = job.RunCreatedAtUtc,
            UpdatedAtUtc = job.RunCreatedAtUtc,
        };
        await runStore.CreateAsync(preExisting, CancellationToken.None);

        var outcome = await dispatcher.RunAsync(job, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(DispatchOutcomeKind.Failed);

        var run = await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, CancellationToken.None);
        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Id).IsEqualTo(job.RunId);
        await Assert.That(run.CreatedAtUtc).IsEqualTo(job.RunCreatedAtUtc);
        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Failed);
        await Assert.That(run.FailureMessage).IsNotNull();

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_RunnerThrowsBeforeCreatingRecord_StillRecordsFailedRun()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dispatcher = new InProcessIngestDispatcher(
            new LocalTempWorkspaceProvider(
                new AppSettings { Ingest = new() { ContentRootPath = contentRoot } },
                tokenProvider,
                new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
                NullLogger<LocalTempWorkspaceProvider>.Instance
            ),
            new RepositoryIngestRunner(
                documentStore,
                new FakeLibraryDocumentStore(),
                runStore,
                NullLogger<RepositoryIngestRunner>.Instance
            ),
            runStore,
            NullLogger<InProcessIngestDispatcher>.Instance
        );

        // A malformed private job (missing LibraryId): workspace acquisition
        // can succeed (it's runtime-agnostic), but the runner's very first
        // statement throws ArgumentException before it ever calls
        // IDocsIngestRunStore.CreateAsync — proving the dispatcher's
        // defensive catch-and-record around the runner call, not just the
        // acquisition-failure path.
        var job = new RepositoryIngestJob
        {
            RunId = $"run_{Guid.NewGuid():N}",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Private,
            RepoUrl = remote.Path,
            OrganizationId = "org_1",
            LibraryId = null,
            Trigger = IngestTriggerReason.Manual,
            Filter = EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

        var outcome = await dispatcher.RunAsync(job, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(DispatchOutcomeKind.Failed);

        var run = await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, CancellationToken.None);
        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Id).IsEqualTo(job.RunId);
        await Assert.That(run.CreatedAtUtc).IsEqualTo(job.RunCreatedAtUtc);
        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Failed);

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_DisposesWorkspaceAfterCompletion()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("guide.md", "# Guide");

        var contentRoot = NewContentRoot();
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var tokenProvider = Substitute.For<IIngestGitHubTokenProvider>();
        tokenProvider
            .GetTokenAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var dispatcher = new InProcessIngestDispatcher(
            new LocalTempWorkspaceProvider(
                new AppSettings { Ingest = new() { ContentRootPath = contentRoot } },
                tokenProvider,
                new GitCommandRunner(NullLogger<GitCommandRunner>.Instance),
                NullLogger<LocalTempWorkspaceProvider>.Instance
            ),
            new RepositoryIngestRunner(
                documentStore,
                new FakeLibraryDocumentStore(),
                runStore,
                NullLogger<RepositoryIngestRunner>.Instance
            ),
            runStore,
            NullLogger<InProcessIngestDispatcher>.Instance
        );

        await dispatcher.RunAsync(PublicJob(remote.Path), CancellationToken.None);

        // Local-temp workspaces delete-on-dispose (spec §5.1); after RunAsync
        // returns, the only thing left under contentRoot should be the empty
        // "public" directory shell, no clone contents.
        var publicRoot = Path.Combine(contentRoot, "public");
        await Assert
            .That(
                Directory.Exists(publicRoot)
                    && Directory.EnumerateFileSystemEntries(publicRoot).Any()
            )
            .IsFalse();

        if (Directory.Exists(contentRoot))
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
