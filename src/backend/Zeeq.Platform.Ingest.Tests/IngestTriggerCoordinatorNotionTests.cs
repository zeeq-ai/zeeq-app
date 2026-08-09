using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

public sealed class IngestTriggerCoordinatorNotionTests
{
    private static readonly IngestSettings Settings = new()
    {
        ManualTriggerWindowSeconds = 3600,
        ManualTriggerMaxInWindow = 5,
    };

    [Test]
    public async Task TryQueueNotionSyncAsync_QueuesRequestedScopeAndNotionViewToken()
    {
        var library = NotionLibrary();
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publisher = new TestMessagePublisher();

        var result = await IngestTriggerCoordinator.TryQueueNotionSyncAsync(
            libraries,
            publisher,
            Settings,
            library,
            ExternalSyncScope.Full,
            IngestTriggerReason.Manual,
            CancellationToken.None
        );

        var queued = result as IngestTriggerResult.Queued;
        await Assert.That(queued).IsNotNull();
        var message = publisher.Published.OfType<NotionSyncRequested>().Single();
        await Assert.That(message.Scope).IsEqualTo(ExternalSyncScope.Full);
        await Assert.That(message.RunId).IsEqualTo(queued!.RunId);
        await Assert.That(library.SyncStatus).IsEqualTo("queued");
        await Assert.That(library.ActiveSyncRunId).IsEqualTo(queued.RunId);
        await Assert.That(queued.ViewToken).IsNotEmpty();

        var decoded = IngestRunViewToken.TryDecode(queued.ViewToken, out _, out var sourceKind);
        await Assert.That(decoded).IsTrue();
        await Assert.That(sourceKind).IsEqualTo(RepositorySourceKind.Notion);
    }

    [Test]
    public async Task TryQueueNotionSyncAsync_WhileIncrementalRunIsActive_ReturnsAlreadyInFlight()
    {
        var library = NotionLibrary(syncStatus: "running");
        var publisher = new TestMessagePublisher();

        var result = await IngestTriggerCoordinator.TryQueueNotionSyncAsync(
            new FakeLibraryDocumentStore { Libraries = { library } },
            publisher,
            Settings,
            library,
            ExternalSyncScope.Full,
            IngestTriggerReason.Manual,
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<IngestTriggerResult.AlreadyInFlight>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task TryQueueNotionSyncAsync_WithoutNotionSource_ReturnsNotSourceBacked()
    {
        var library = new Library
        {
            Id = "library_1",
            OrganizationId = "org_1",
            Name = "docs",
            SourceKind = RepositorySourceKind.Notion.ToString(),
            SyncStatus = "idle",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var result = await IngestTriggerCoordinator.TryQueueNotionSyncAsync(
            new FakeLibraryDocumentStore { Libraries = { library } },
            new TestMessagePublisher(),
            Settings,
            library,
            ExternalSyncScope.Incremental,
            IngestTriggerReason.Manual,
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<IngestTriggerResult.NotSourceBacked>();
    }

    [Test]
    public async Task TryQueueNotionSyncAsync_WhenRateLimited_DoesNotPublish()
    {
        var now = DateTimeOffset.UtcNow;
        var library = NotionLibrary(
            manualTriggerHistory:
            [
                now.AddMinutes(-50),
                now.AddMinutes(-40),
                now.AddMinutes(-30),
                now.AddMinutes(-20),
                now.AddMinutes(-10),
            ]
        );
        var publisher = new TestMessagePublisher();

        var result = await IngestTriggerCoordinator.TryQueueNotionSyncAsync(
            new FakeLibraryDocumentStore { Libraries = { library } },
            publisher,
            Settings,
            library,
            ExternalSyncScope.Incremental,
            IngestTriggerReason.Manual,
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<IngestTriggerResult.RateLimited>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    private static Library NotionLibrary(
        string syncStatus = "idle",
        DateTimeOffset[]? manualTriggerHistory = null
    ) =>
        new()
        {
            Id = "library_1",
            OrganizationId = "org_1",
            TeamId = "team_1",
            Name = "docs",
            SourceKind = RepositorySourceKind.Notion.ToString(),
            ExternalSource = new LibraryExternalSource
            {
                Notion = new NotionSourceConfiguration { AccessTokenValueId = "value_1" },
            },
            SyncStatus = syncStatus,
            ManualTriggerHistory = manualTriggerHistory ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
}
