using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="PrivateRepositorySyncRequestedHandler"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/PrivateRepositorySyncRequestedHandlerTests/*"
/// </summary>
public sealed class PrivateRepositorySyncRequestedHandlerTests
{
    private static readonly IngestSettings Settings = new()
    {
        SyncIntervalSeconds = 3600,
        SyncJitterFraction = 0.2,
    };

    private static Library Library(string syncStatus = "idle") =>
        new()
        {
            Id = "lib_1",
            OrganizationId = "org_1",
            Name = "docs",
            SourceKind = "GitHub",
            SourceRepoUrl = "https://github.com/acme/private-docs",
            IncludeFilters = ["docs/**"],
            SyncStatus = syncStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static PrivateRepositorySyncRequested Message(Library library)
    {
        var runCreatedAtUtc = DateTimeOffset.UtcNow;
        library.ActiveSyncRunId = "run_1";
        library.ActiveSyncRunCreatedAtUtc = runCreatedAtUtc;
        library.SyncQueuedAtUtc = runCreatedAtUtc;

        return new()
        {
            RunId = "run_1",
            RunCreatedAtUtc = runCreatedAtUtc,
            OrganizationId = library.OrganizationId,
            LibraryId = library.Id,
            RepoUrl = library.SourceRepoUrl!,
            Trigger = IngestTriggerReason.Manual,
            TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
        };
    }

    private static PrivateRepositorySyncRequestedHandler Handler(
        FakeLibraryDocumentStore libraries,
        IRepositoryIngestDispatcher dispatcher,
        IGitHubInstallationStore? installations = null
    ) =>
        new(
            Substitute.For<IDeadLetterWriter>(),
            libraries,
            installations ?? Substitute.For<IGitHubInstallationStore>(),
            dispatcher,
            Settings,
            NullLogger<PrivateRepositorySyncRequestedHandler>.Instance
        );

    [Test]
    public async Task HandleAsync_LibraryNotFound_NoOps()
    {
        var libraries = new FakeLibraryDocumentStore();
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var handler = Handler(libraries, dispatcher);
        var message = Message(Library());

        await handler.HandleAsync(message, CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_StaleMessage_NoOps()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var handler = Handler(libraries, dispatcher);
        var message = Message(library);
        library.ActiveSyncRunId = "run_newer";

        await handler.HandleAsync(message, CancellationToken.None);

        await dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>());
        await Assert.That(library.SyncStatus).IsEqualTo("queued");
    }

    [Test]
    public async Task HandleAsync_Completed_SetsSourceSyncedAtAndSchedulesNextSync()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(libraries, dispatcher);

        var before = DateTimeOffset.UtcNow;
        await handler.HandleAsync(Message(library), CancellationToken.None);

        await Assert.That(library.SyncStatus).IsEqualTo("idle");
        await Assert.That(library.SourceSyncedAt).IsNotNull();
        await Assert.That(library.SourceSyncedAt!.Value).IsGreaterThanOrEqualTo(before);
        await Assert.That(library.NextSyncAt).IsNotNull();
        await Assert.That(library.NextSyncAt!.Value).IsGreaterThan(before);
    }

    [Test]
    public async Task HandleAsync_CompletionAfterNewerRunClaimed_DoesNotClearNewerLease()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        var handler = Handler(libraries, dispatcher);
        var message = Message(library);
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                library.ActiveSyncRunId = "run_newer";
                library.ActiveSyncRunCreatedAtUtc = DateTimeOffset.UtcNow;
                library.SyncStatus = "queued";
                return new DispatchOutcome(DispatchOutcomeKind.Completed);
            });

        await handler.HandleAsync(message, CancellationToken.None);

        await Assert.That(library.SyncStatus).IsEqualTo("queued");
        await Assert.That(library.ActiveSyncRunId).IsEqualTo("run_newer");
    }

    [Test]
    public async Task HandleAsync_BuildsJobFromLibraryFilters()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var handler = Handler(libraries, dispatcher);
        var message = Message(library);

        await handler.HandleAsync(message, CancellationToken.None);

        await dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<RepositoryIngestJob>(job =>
                    job.RunId == message.RunId
                    && job.Kind == RepositorySourceKind.Private
                    && job.OrganizationId == library.OrganizationId
                    && job.LibraryId == library.Id
                    && job.RepoUrl == library.SourceRepoUrl
                    && job.Filter.IncludeGlobs.SequenceEqual(library.IncludeFilters)
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_ResolvesInstallationIdFromStore()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Completed));
        var installations = Substitute.For<IGitHubInstallationStore>();
        installations
            .FindActiveForOrganizationAsync(library.OrganizationId, Arg.Any<CancellationToken>())
            .Returns(
                new GitHubAppInstallation
                {
                    Id = "ghi_1",
                    OrganizationId = library.OrganizationId,
                    InstallationId = 42L,
                    AccountLogin = "acme",
                    AccountType = "Organization",
                    RepositorySelection = "all",
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                }
            );
        var handler = Handler(libraries, dispatcher, installations);

        await handler.HandleAsync(Message(library), CancellationToken.None);

        await dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<RepositoryIngestJob>(job => job.InstallationId == 42L),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_Failed_LogsAndResetsToIdleWithoutSettingSourceSyncedAt()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new DispatchOutcome(DispatchOutcomeKind.Failed, "clone failed"));
        var handler = Handler(libraries, dispatcher);

        await handler.HandleAsync(Message(library), CancellationToken.None);

        await Assert.That(library.SyncStatus).IsEqualTo("idle");
        await Assert.That(library.SourceSyncedAt).IsNull();
        await Assert.That(library.NextSyncAt).IsNotNull();
    }

    [Test]
    public async Task HandleAsync_DispatcherThrows_DoesNotRethrowAndResetsToIdle()
    {
        var library = Library(syncStatus: "queued");
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var dispatcher = Substitute.For<IRepositoryIngestDispatcher>();
        dispatcher
            .RunAsync(Arg.Any<RepositoryIngestJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<DispatchOutcome>>(_ => throw new InvalidOperationException("boom"));
        var handler = Handler(libraries, dispatcher);

        await handler.HandleAsync(Message(library), CancellationToken.None);

        await Assert.That(library.SyncStatus).IsEqualTo("idle");
    }
}
