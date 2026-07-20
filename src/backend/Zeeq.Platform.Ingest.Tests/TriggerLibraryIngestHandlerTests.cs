using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="TriggerLibraryIngestHandler"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/TriggerLibraryIngestHandlerTests/*"
/// </summary>
public sealed class TriggerLibraryIngestHandlerTests
{
    private static readonly IngestSettings Settings = new()
    {
        ManualTriggerWindowSeconds = 3600,
        ManualTriggerMaxInWindow = 5,
    };

    private static Library PrivateLibrary(
        string syncStatus = "idle",
        DateTimeOffset[]? manualTriggerHistory = null
    ) =>
        new()
        {
            Id = "library_1",
            OrganizationId = "org_1",
            Name = "docs",
            SourceKind = "GitHub",
            SourceRepoUrl = "https://github.com/acme/docs",
            SyncStatus = syncStatus,
            ManualTriggerHistory = manualTriggerHistory ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    [Test]
    public async Task HandleAsync_LibraryNotFound_ReturnsNotFound()
    {
        var libraries = new FakeLibraryDocumentStore();
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "missing",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_LibraryHasNoSource_ReturnsBadRequest()
    {
        var localLibrary = new Library
        {
            Id = "library_1",
            OrganizationId = "org_1",
            Name = "docs",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var libraries = new FakeLibraryDocumentStore { Libraries = { localLibrary } };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<IngestError>>();
    }

    [Test]
    public async Task HandleAsync_AlreadyQueued_ReturnsConflict()
    {
        var libraries = new FakeLibraryDocumentStore
        {
            Libraries = { PrivateLibrary(syncStatus: "running") },
        };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Conflict<IngestError>>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_ValidTrigger_PublishesMessageAndUpdatesSyncState()
    {
        var library = PrivateLibrary();
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var ok = (Ok<TriggerIngestRunResponse>)result.Result;
        await Assert.That(ok.Value!.RunId).StartsWith("run_");
        await Assert.That(ok.Value.ViewToken).IsNotEmpty();

        var published = publisher.Published.OfType<PrivateRepositorySyncRequested>().Single();
        await Assert.That(published.OrganizationId).IsEqualTo("org_1");
        await Assert.That(published.LibraryId).IsEqualTo("library_1");
        await Assert.That(published.RepoUrl).IsEqualTo("https://github.com/acme/docs");
        await Assert.That(published.RunId).IsEqualTo(ok.Value.RunId);
        await Assert.That(published.RunCreatedAtUtc).IsEqualTo(ok.Value.RunCreatedAtUtc);
        await AssertPostgresMicrosecondPrecisionAsync(published.RunCreatedAtUtc);
        await Assert.That(published.Trigger).IsEqualTo(IngestTriggerReason.Manual);

        await Assert.That(library.SyncStatus).IsEqualTo("queued");
        await Assert.That(library.ActiveSyncRunId).IsEqualTo(ok.Value.RunId);
        await Assert.That(library.ActiveSyncRunCreatedAtUtc).IsEqualTo(ok.Value.RunCreatedAtUtc);
        await Assert.That(library.SyncQueuedAtUtc).IsNotNull();
        await AssertPostgresMicrosecondPrecisionAsync(library.ActiveSyncRunCreatedAtUtc!.Value);
        await AssertPostgresMicrosecondPrecisionAsync(library.SyncQueuedAtUtc!.Value);
        await Assert.That(library.SyncStartedAtUtc).IsNull();
        await Assert.That(library.ManualTriggerHistory.Length).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_RateLimitExceeded_ReturnsTooManyRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var library = PrivateLibrary(
            manualTriggerHistory:
            [
                now.AddMinutes(-50),
                now.AddMinutes(-40),
                now.AddMinutes(-30),
                now.AddMinutes(-20),
                now.AddMinutes(-10),
            ]
        );
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<JsonHttpResult<IngestError>>();
        var json = (JsonHttpResult<IngestError>)result.Result;
        await Assert.That(json.StatusCode).IsEqualTo(StatusCodes.Status429TooManyRequests);
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_TriggerNearWindowEdge_StillCountsTowardLimit()
    {
        // Locks in the `<=` boundary comparison: an entry just inside the
        // window (not just comfortably old) still counts. True tick-exact
        // boundary equality isn't testable without injecting a TimeProvider
        // into the handler — not worth adding for this edge case — so this
        // gets as close to the boundary as a real DateTimeOffset.UtcNow call
        // inside the handler allows.
        var now = DateTimeOffset.UtcNow;
        var library = PrivateLibrary(
            manualTriggerHistory:
            [
                now.AddSeconds(-(Settings.ManualTriggerWindowSeconds - 1)),
                now.AddMinutes(-40),
                now.AddMinutes(-30),
                now.AddMinutes(-20),
            ]
        );
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        // 4 counted + this attempt would be the 5th allowed — still under the
        // cap of 5, so it should succeed and the near-edge entry should
        // survive into the trimmed history.
        await Assert.That(result.Result).IsTypeOf<Ok<TriggerIngestRunResponse>>();
        await Assert.That(library.ManualTriggerHistory.Length).IsEqualTo(5);
    }

    [Test]
    public async Task HandleAsync_PublicSourceBackedLibrary_TriggersPublicSourceSync()
    {
        // A library subscribing to a public source (the library-GitHub-import
        // UI slice) triggers through this same org-scoped endpoint rather than
        // the admin-only TriggerPublicSourceIngestHandler — any org user owns
        // their own library's sync trigger regardless of which kind of source
        // backs it.
        var source = new DocsPublicSource
        {
            Id = "pubsrc_1",
            RepoUrl = "https://github.com/acme/public-docs",
            Name = "public-docs",
            SyncStatus = "idle",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var library = new Library
        {
            Id = "library_2",
            OrganizationId = "org_1",
            Name = "public-docs-subscription",
            PublicSourceId = "pubsrc_1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore { Sources = { source } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "public-docs-subscription",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Ok<TriggerIngestRunResponse>>();
        var published = publisher.Published.OfType<PublicRepositorySyncRequested>().Single();
        await Assert.That(published.PublicSourceId).IsEqualTo("pubsrc_1");
        await Assert.That(published.RepoUrl).IsEqualTo("https://github.com/acme/public-docs");
        await AssertPostgresMicrosecondPrecisionAsync(published.RunCreatedAtUtc);
        await Assert.That(source.SyncStatus).IsEqualTo("queued");
        await AssertPostgresMicrosecondPrecisionAsync(source.ActiveSyncRunCreatedAtUtc!.Value);
        await AssertPostgresMicrosecondPrecisionAsync(source.SyncQueuedAtUtc!.Value);
    }

    [Test]
    public async Task HandleAsync_PublicSourceBackedLibrary_QuarantinedSource_ReturnsConflict()
    {
        var source = new DocsPublicSource
        {
            Id = "pubsrc_1",
            RepoUrl = "https://github.com/acme/public-docs",
            Name = "public-docs",
            SyncStatus = "idle",
            Status = "quarantined",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var library = new Library
        {
            Id = "library_2",
            OrganizationId = "org_1",
            Name = "public-docs-subscription",
            PublicSourceId = "pubsrc_1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore { Sources = { source } };
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "public-docs-subscription",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Conflict<IngestError>>();
        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_OldTriggersOutsideWindow_AreNotCountedTowardLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var library = PrivateLibrary(
            manualTriggerHistory:
            [
                now.AddHours(-3),
                now.AddHours(-2),
                now.AddHours(-1).AddMinutes(-5),
            ]
        );
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var publicSources = new FakeDocsPublicSourceStore();
        var publisher = new TestMessagePublisher();
        var handler = new TriggerLibraryIngestHandler(
            libraries,
            publicSources,
            publisher,
            Settings
        );

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Ok<TriggerIngestRunResponse>>();
        // The three stale entries age out; only the fresh trigger remains.
        await Assert.That(library.ManualTriggerHistory.Length).IsEqualTo(1);
    }

    private static async Task AssertPostgresMicrosecondPrecisionAsync(DateTimeOffset value) =>
        await Assert.That(value.Ticks % TimeSpan.TicksPerMicrosecond).IsEqualTo(0);
}
