using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="ResetLibraryIngestRunStateHandler"/>.
/// </summary>
public sealed class ResetLibraryIngestRunStateHandlerTests
{
    private static Library PrivateLibrary() =>
        new()
        {
            Id = "library_1",
            OrganizationId = "org_1",
            Name = "docs",
            SourceKind = "GitHub",
            SourceRepoUrl = "https://github.com/acme/docs",
            SyncStatus = "running",
            ActiveSyncRunId = "run_1",
            ActiveSyncRunCreatedAtUtc = DateTimeOffset.UtcNow,
            SyncQueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            SyncStartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-9),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    [Test]
    public async Task HandleAsync_PrivateLibrary_ClearsStateAndMarksRunStalled()
    {
        var library = PrivateLibrary();
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var handler = new ResetLibraryIngestRunStateHandler(libraries);

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var ok = (Ok<ResetLibraryIngestRunStateResponse>)result.Result;
        await Assert.That(ok.Value!.SyncStatus).IsEqualTo("idle");
        await Assert.That(ok.Value.RunMarkedStalled).IsTrue();
        await Assert.That(library.ActiveSyncRunId).IsNull();
    }

    [Test]
    public async Task HandleAsync_PublicSourceLibrary_ReturnsBadRequest()
    {
        var library = new Library
        {
            Id = "library_1",
            OrganizationId = "org_1",
            Name = "docs",
            PublicSourceId = "src_1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var handler = new ResetLibraryIngestRunStateHandler(libraries);

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<IngestError>>();
    }

    [Test]
    public async Task HandleAsync_PrivateLibraryWithoutActiveSync_ReturnsBadRequest()
    {
        var library = PrivateLibrary();
        library.SyncStatus = "idle";
        library.ActiveSyncRunId = null;
        library.ActiveSyncRunCreatedAtUtc = null;
        var libraries = new FakeLibraryDocumentStore { Libraries = { library } };
        var handler = new ResetLibraryIngestRunStateHandler(libraries);

        var result = await handler.HandleAsync(
            "org_1",
            "docs",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<IngestError>>();
    }
}
