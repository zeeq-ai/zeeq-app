using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Integrations.GitHub;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="PublicRepositorySyncRequestedHandler"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/PublicRepositorySyncRequestedHandlerTests/*"
/// </summary>
public sealed class PublicRepositorySyncRequestedHandlerTests
{
    private static readonly IngestSettings Settings = new()
    {
        SyncIntervalSeconds = 3600,
        SyncJitterFraction = 0.2,
    };

    private static DocsPublicSource Source(string syncStatus = "idle", string status = "active") =>
        new()
        {
            Id = "src_1",
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/docs",
            Name = "Example Docs",
            DefaultIncludeFilters = ["docs/**"],
            SyncStatus = syncStatus,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static PublicRepositorySyncRequested Message(DocsPublicSource source) =>
        new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            PublicSourceId = source.Id,
            RepoUrl = source.RepoUrl,
            Trigger = IngestTriggerReason.Manual,
            TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
        };

    private static PublicRepositorySyncRequestedHandler Handler(
        FakeDocsPublicSourceStore sources,
        IRepositoryIngestDispatcher dispatcher,
        ILibraryDocumentStore? libraries = null,
        IPublicRepositoryVisibilityChecker? visibilityChecker = null
    ) =>
        new(
            Substitute.For<IDeadLetterWriter>(),
            sources,
            libraries ?? new FakeLibraryDocumentStore(),
            visibilityChecker ?? AlwaysPublic(),
            dispatcher,
            Settings,
            NullLogger<PublicRepositorySyncRequestedHandler>.Instance
        );

    private static IPublicRepositoryVisibilityChecker AlwaysPublic()
    {
        var checker = Substitute.For<IPublicRepositoryVisibilityChecker>();
        checker
            .CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RepositoryVisibilityCheckResult.Public);
        return checker;
    }

    [Test]
    public async Task HandleAsync_SourceNotFound_NoOps()
    {
        var sources = new FakeDocsPublicSourceStore();
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var handler = Handler(sources, dispatcher);
        var message = Message(Source());

        await handler.HandleAsync(message, CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_Quarantined_SkipsDispatchAndResetsToIdle()
    {
        var source = Source(status: "quarantined", syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var handler = Handler(sources, dispatcher);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
        await Assert.That(source.SyncStatus).IsEqualTo("idle");
    }

    [Test]
    public async Task HandleAsync_Completed_SetsSyncedAtAndSchedulesNextSync()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(sources, dispatcher);

        var before = DateTimeOffset.UtcNow;
        await handler.HandleAsync(Message(source), CancellationToken.None);

        await Assert.That(source.SyncStatus).IsEqualTo("idle");
        await Assert.That(source.SyncedAt).IsNotNull();
        await Assert.That(source.SyncedAt!.Value).IsGreaterThanOrEqualTo(before);
        await Assert.That(source.NextSyncAt).IsNotNull();
        await Assert.That(source.NextSyncAt!.Value).IsGreaterThan(before);
    }

    [Test]
    public async Task HandleAsync_SingleSubscriberWithNoOverride_UsesSourceDefaults()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var libraries = new FakeLibraryDocumentStore
        {
            Libraries =
            {
                LibraryBuilder.ForPublicSource(source.Id).Build("lib_1", "org_a", "lib-a"),
            },
        };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(sources, dispatcher, libraries);
        var message = Message(source);

        await handler.HandleAsync(message, CancellationToken.None);

        await dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<RepositoryIngestJob>(job =>
                    job.RunId == message.RunId
                    && job.Kind == RepositorySourceKind.Public
                    && job.PublicSourceId == source.Id
                    && job.RepoUrl == source.RepoUrl
                    && job.Filter.IncludeGlobs.SequenceEqual(source.DefaultIncludeFilters)
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_MultipleSubscribers_UnionsEffectiveFilters()
    {
        // org_a overrides its include filter; org_b has no override and falls
        // back to the source default. The run's effective filter must cover
        // both — narrowing for org_a must not exclude org_b's still-in-scope
        // files (spec §2.4).
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var libraries = new FakeLibraryDocumentStore
        {
            Libraries =
            {
                LibraryBuilder
                    .ForPublicSource(source.Id)
                    .WithIncludeFilter("org-a-only/**")
                    .Build("lib_a", "org_a", "lib-a"),
                LibraryBuilder.ForPublicSource(source.Id).Build("lib_b", "org_b", "lib-b"),
            },
        };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(sources, dispatcher, libraries);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<RepositoryIngestJob>(job =>
                    job.Filter.IncludeGlobs.Length == 2
                    && job.Filter.IncludeGlobs.Contains("org-a-only/**")
                    && job.Filter.IncludeGlobs.Contains("docs/**")
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_NoSubscribers_FallsBackToSourceDefaults()
    {
        // Zero subscribers is reachable (a source's last library can be
        // deleted, and ClaimDueForSyncAsync has no subscriber-count check),
        // so an empty union must not mean "include everything" — that would
        // ingest the whole upstream repo for an orphaned source nobody
        // queries. Falling back to the source's own defaults bounds the
        // waste to what it was before the union-filter logic existed.
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(sources, dispatcher);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<RepositoryIngestJob>(job =>
                    job.Filter.IncludeGlobs.SequenceEqual(source.DefaultIncludeFilters)
                    && job.Filter.ExcludeGlobs.SequenceEqual(source.DefaultExcludeFilters)
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_VisibilityCheckNotPubliclyAccessible_QuarantinesAndSkipsDispatch()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var checker = Substitute.For<IPublicRepositoryVisibilityChecker>();
        checker
            .CheckAsync(source.RepoUrl, Arg.Any<CancellationToken>())
            .Returns(RepositoryVisibilityCheckResult.NotPubliclyAccessible);
        var handler = Handler(sources, dispatcher, visibilityChecker: checker);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
        await Assert.That(source.Status).IsEqualTo("quarantined");
        await Assert.That(source.SyncStatus).IsEqualTo("idle");
    }

    [Test]
    public async Task HandleAsync_RealCheckerGets404_QuarantinesEndToEnd()
    {
        // Composes the real PublicRepositoryVisibilityChecker (not a
        // substitute standing in for it) so the actual NotFoundException ->
        // NotPubliclyAccessible -> quarantine chain is exercised through the
        // handler, not just asserted at the checker's own unit tests.
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var realChecker = new PublicRepositoryVisibilityChecker(
            new FakeGitHubRepositoryVisibilityClient
            {
                ExceptionToThrow = new Octokit.NotFoundException(
                    "not found",
                    System.Net.HttpStatusCode.NotFound
                ),
            },
            NullLogger<PublicRepositoryVisibilityChecker>.Instance
        );
        var handler = Handler(sources, dispatcher, visibilityChecker: realChecker);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
        await Assert.That(source.Status).IsEqualTo("quarantined");
        await Assert.That(source.SyncStatus).IsEqualTo("idle");
    }

    private sealed class FakeGitHubRepositoryVisibilityClient : IGitHubRepositoryVisibilityClient
    {
        public Exception? ExceptionToThrow { get; init; }

        public Task<bool> IsPrivateAsync(
            string owner,
            string name,
            CancellationToken cancellationToken
        ) =>
            ExceptionToThrow is not null
                ? throw ExceptionToThrow
                : Task.FromResult(false);
    }

    [Test]
    public async Task HandleAsync_VisibilityCheckTransientError_SkipsDispatchWithoutQuarantining()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var checker = Substitute.For<IPublicRepositoryVisibilityChecker>();
        checker
            .CheckAsync(source.RepoUrl, Arg.Any<CancellationToken>())
            .Returns(RepositoryVisibilityCheckResult.TransientError);
        var handler = Handler(sources, dispatcher, visibilityChecker: checker);

        var before = DateTimeOffset.UtcNow;
        await handler.HandleAsync(Message(source), CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
        await Assert.That(source.Status).IsEqualTo("active");
        await Assert.That(source.SyncStatus).IsEqualTo("idle");
        await Assert.That(source.NextSyncAt).IsNotNull();
        await Assert.That(source.NextSyncAt!.Value).IsGreaterThan(before);
    }

    [Test]
    public async Task HandleAsync_Failed_LogsAndResetsToIdleWithoutSettingSyncedAt()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Failed, "clone failed"));
        var handler = Handler(sources, dispatcher);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await Assert.That(source.SyncStatus).IsEqualTo("idle");
        await Assert.That(source.SyncedAt).IsNull();
        await Assert.That(source.NextSyncAt).IsNotNull();
    }

    [Test]
    public async Task HandleAsync_DispatcherThrows_DoesNotRethrowAndResetsToIdle()
    {
        var source = Source(syncStatus: "queued");
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<DispatchOutcome>>(_ => throw new InvalidOperationException("boom"));
        var handler = Handler(sources, dispatcher);

        await handler.HandleAsync(Message(source), CancellationToken.None);

        await Assert.That(source.SyncStatus).IsEqualTo("idle");
    }
}
