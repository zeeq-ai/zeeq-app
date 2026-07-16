using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="TriggerPublicSourceIngestHandler"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/TriggerPublicSourceIngestHandlerTests/*"
/// </summary>
public sealed class TriggerPublicSourceIngestHandlerTests
{
    private static readonly IngestSettings Settings = new()
    {
        ManualTriggerWindowSeconds = 3600,
        ManualTriggerMaxInWindow = 5,
    };

    private static DocsPublicSource Source(
        string syncStatus = "idle",
        string status = "active",
        DateTimeOffset[]? manualTriggerHistory = null
    ) =>
        new()
        {
            Id = "src_1",
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/docs",
            Name = "Example Docs",
            SyncStatus = syncStatus,
            Status = status,
            ManualTriggerHistory = manualTriggerHistory ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    [Test]
    public async Task HandleAsync_SourceNotFound_ReturnsNotFound()
    {
        var sources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("missing", CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_AlreadyRunning_ReturnsConflict()
    {
        var sources = new FakeDocsPublicSourceStore { Sources = { Source(syncStatus: "running") } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("src_1", CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<Conflict<IngestError>>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_Quarantined_ReturnsConflict()
    {
        var sources = new FakeDocsPublicSourceStore { Sources = { Source(status: "quarantined") } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("src_1", CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<Conflict<IngestError>>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_ValidTrigger_PublishesMessageAndUpdatesSyncState()
    {
        var source = Source();
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("src_1", CancellationToken.None);

        var ok = (Ok<TriggerIngestRunResponse>)result.Result;
        await Assert.That(ok.Value!.RunId).StartsWith("run_");

        var published = publisher.Published.OfType<PublicRepositorySyncRequested>().Single();
        await Assert.That(published.PublicSourceId).IsEqualTo("src_1");
        await Assert.That(published.RepoUrl).IsEqualTo("https://github.com/example/docs");
        await Assert.That(published.RunId).IsEqualTo(ok.Value.RunId);
        await Assert.That(published.RunCreatedAtUtc).IsEqualTo(ok.Value.RunCreatedAtUtc);

        await Assert.That(source.SyncStatus).IsEqualTo("queued");
        await Assert.That(source.ManualTriggerHistory.Length).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_TriggerNearWindowEdge_StillCountsTowardLimit()
    {
        // See the equivalent test on TriggerLibraryIngestHandlerTests for why
        // this approximates rather than hits the exact tick boundary.
        var now = DateTimeOffset.UtcNow;
        var source = Source(
            manualTriggerHistory:
            [
                now.AddSeconds(-(Settings.ManualTriggerWindowSeconds - 1)),
                now.AddMinutes(-40),
                now.AddMinutes(-30),
                now.AddMinutes(-20),
            ]
        );
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("src_1", CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<Ok<TriggerIngestRunResponse>>();
        await Assert.That(source.ManualTriggerHistory.Length).IsEqualTo(5);
    }

    [Test]
    public async Task HandleAsync_RateLimitExceeded_ReturnsTooManyRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Source(
            manualTriggerHistory:
            [
                now.AddMinutes(-50),
                now.AddMinutes(-40),
                now.AddMinutes(-30),
                now.AddMinutes(-20),
                now.AddMinutes(-10),
            ]
        );
        var sources = new FakeDocsPublicSourceStore { Sources = { source } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerPublicSourceIngestHandler(sources, publisher, Settings);

        var result = await handler.HandleAsync("src_1", CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<JsonHttpResult<IngestError>>();
        var json = (JsonHttpResult<IngestError>)result.Result;
        await Assert.That(json.StatusCode).IsEqualTo(StatusCodes.Status429TooManyRequests);
        await Assert.That(publisher.Published).IsEmpty();
    }
}
