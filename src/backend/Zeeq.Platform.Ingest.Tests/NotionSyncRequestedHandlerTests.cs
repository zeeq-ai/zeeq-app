using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/NotionSyncRequestedHandlerTests/*"
/// </summary>
public sealed class NotionSyncRequestedHandlerTests
{
    private static readonly DateTimeOffset RunCreatedAt = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeLibraryDocumentStore _libraries = new();
    private readonly INotionIngestDispatcher _dispatcher =
        Substitute.For<INotionIngestDispatcher>();

    [Test]
    public async Task HandleAsync_StaleRun_DoesNotDispatch()
    {
        var library = Library();
        library.ActiveSyncRunId = "newer-run";
        _libraries.Libraries.Add(library);

        await Handler().HandleAsync(Message(ExternalSyncScope.Incremental), default);

        await _dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<NotionIngestJob>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_DuplicateDeliveryAfterRunStarted_DoesNotDispatch()
    {
        var library = Library();
        library.SyncStatus = "running";
        _libraries.Libraries.Add(library);

        await Handler().HandleAsync(Message(ExternalSyncScope.Incremental), default);

        await _dispatcher
            .DidNotReceive()
            .RunAsync(Arg.Any<NotionIngestJob>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_SucceededFullRun_AdvancesFullResyncBackstop()
    {
        var library = Library();
        var previousFullResync = library.NextFullResyncAt;
        _libraries.Libraries.Add(library);
        _dispatcher
            .RunAsync(Arg.Any<NotionIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new NotionDispatchOutcome(IngestRunStatus.Succeeded));
        var before = DateTimeOffset.UtcNow;

        await Handler().HandleAsync(Message(ExternalSyncScope.Full), default);

        await Assert.That(library.SyncStatus).IsEqualTo("idle");
        await Assert.That(library.NextFullResyncAt).IsNotEqualTo(previousFullResync);
        await Assert.That(library.NextFullResyncAt).IsNotNull();
        await Assert.That(library.NextFullResyncAt!.Value).IsGreaterThan(before.AddSeconds(119));
        await Assert.That(library.SourceSyncedAt).IsNotNull();
    }

    [Test]
    public async Task HandleAsync_PartialFullRun_PreservesDueFullResync()
    {
        var library = Library();
        var dueAt = library.NextFullResyncAt;
        _libraries.Libraries.Add(library);
        _dispatcher
            .RunAsync(Arg.Any<NotionIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new NotionDispatchOutcome(IngestRunStatus.Partial, "one page failed"));

        await Handler().HandleAsync(Message(ExternalSyncScope.Full), default);

        await Assert.That(library.NextFullResyncAt).IsEqualTo(dueAt);
        await Assert.That(library.SyncStatus).IsEqualTo("idle");
    }

    [Test]
    public async Task HandleAsync_IncrementalRun_PreservesFullResyncBackstop()
    {
        var library = Library();
        var dueAt = library.NextFullResyncAt;
        _libraries.Libraries.Add(library);
        _dispatcher
            .RunAsync(Arg.Any<NotionIngestJob>(), Arg.Any<CancellationToken>())
            .Returns(new NotionDispatchOutcome(IngestRunStatus.Succeeded));

        await Handler().HandleAsync(Message(ExternalSyncScope.Incremental), default);

        await Assert.That(library.NextFullResyncAt).IsEqualTo(dueAt);
        await _dispatcher
            .Received(1)
            .RunAsync(
                Arg.Is<NotionIngestJob>(job =>
                    job.Scope == ExternalSyncScope.Incremental
                    && job.SourceReference == "notion://library/library-1"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    private NotionSyncRequestedHandler Handler() =>
        new(
            Substitute.For<IDeadLetterWriter>(),
            _libraries,
            _dispatcher,
            new IngestSettings
            {
                SyncIntervalSeconds = 60,
                NotionFullResyncIntervalSeconds = 120,
                SyncJitterFraction = 0,
            },
            NullLogger<NotionSyncRequestedHandler>.Instance
        );

    private static Library Library() =>
        new()
        {
            Id = "library-1",
            OrganizationId = "org-1",
            Name = "Notion",
            SourceKind = RepositorySourceKind.Notion.ToString(),
            SyncStatus = "queued",
            ActiveSyncRunId = "run-1",
            ActiveSyncRunCreatedAtUtc = RunCreatedAt,
            SyncQueuedAtUtc = RunCreatedAt,
            NextSyncAt = RunCreatedAt,
            NextFullResyncAt = RunCreatedAt,
            ExternalSource = new LibraryExternalSource
            {
                Notion = new NotionSourceConfiguration
                {
                    AccessTokenValueId = "enc-1",
                    CallbackTokenSerial = 1,
                },
            },
            CreatedAt = RunCreatedAt,
            UpdatedAt = RunCreatedAt,
        };

    private static NotionSyncRequested Message(ExternalSyncScope scope) =>
        new()
        {
            OrganizationId = "org-1",
            LibraryId = "library-1",
            RunId = "run-1",
            RunCreatedAtUtc = RunCreatedAt,
            Scope = scope,
            Trigger = IngestTriggerReason.Scheduled,
            TraceContext = new ZeeqTraceContext(null, null),
        };
}
