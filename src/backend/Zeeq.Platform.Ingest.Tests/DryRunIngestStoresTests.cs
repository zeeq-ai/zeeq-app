using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Platform.Ingest.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for the <c>Zeeq.Platform.Ingest.Diagnostics</c> dry-run stores —
/// confirms real reads flow through while writes are absorbed, not persisted.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/DryRunIngestStoresTests/*"
/// </summary>
public sealed class DryRunIngestStoresTests
{
    private static RepositoryIngestJob PublicJob(EffectiveFilter? filter = null) =>
        new()
        {
            RunId = $"run_{Guid.CreateVersion7():N}",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/docs",
            PublicSourceId = "src_1",
            Trigger = IngestTriggerReason.Manual,
            Filter = filter ?? EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    [Test]
    public async Task RunAsync_DryRun_DoesNotPersistToRealStore()
    {
        var realDocumentStore = new FakeDocsPublicDocumentStore();
        var realLibraryStore = new FakeLibraryDocumentStore();

        var runner = DryRunIngestRunnerFactory.Create(
            realDocumentStore,
            realLibraryStore,
            NullLoggerFactory.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("guide.md", "# Guide\n\nBody one.");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesAdded).IsEqualTo(1);

        // The whole point: nothing landed in the real store.
        await Assert.That(realDocumentStore.Documents).IsEmpty();
    }

    [Test]
    public async Task RunAsync_DryRun_ReadsRealExistingStateForMoveDetection()
    {
        var realDocumentStore = new FakeDocsPublicDocumentStore();
        realDocumentStore.Documents.Add(
            new DocsPublicDocument
            {
                Id = "doc_real",
                PublicSourceId = "src_1",
                Path = "/old-location/guide.md",
                Title = "Guide",
                TitleNormalized = "guide",
                Content = "# Guide\n\nStable body.",
                ContentHash = Convert
                    .ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes("# Guide\n\nStable body.")
                        )
                    )
                    .ToLowerInvariant(),
                SyncRunId = "run_prior",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            }
        );

        var runner = DryRunIngestRunnerFactory.Create(
            realDocumentStore,
            new FakeLibraryDocumentStore(),
            NullLoggerFactory.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("new-location/guide.md", "# Guide\n\nStable body.");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        // Move detected against the REAL existing row, even though nothing
        // is written back — proves reads flow through to the real store.
        await Assert.That(run.FilesMoved).IsEqualTo(1);

        // The real store's row is untouched — still at its original path.
        await Assert
            .That(realDocumentStore.Documents.Single().Path)
            .IsEqualTo("/old-location/guide.md");
    }

    [Test]
    public async Task RunAsync_DryRun_CleanPass_ReportsSweepWithoutDeletingFromRealStore()
    {
        var realDocumentStore = new FakeDocsPublicDocumentStore();
        realDocumentStore.Documents.Add(
            new DocsPublicDocument
            {
                Id = "doc_removed",
                PublicSourceId = "src_1",
                Path = "/removed.md",
                Title = "Removed",
                TitleNormalized = "removed",
                Content = "# Removed\n\nBody.",
                ContentHash = "removed-hash",
                SyncRunId = "run_prior",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            }
        );

        var runner = DryRunIngestRunnerFactory.Create(
            realDocumentStore,
            new FakeLibraryDocumentStore(),
            NullLoggerFactory.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("kept.md", "# Kept\n\nBody.");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        await Assert.That(run.FilesDeleted).IsEqualTo(1);
        await Assert.That(realDocumentStore.Documents.Single().Path).IsEqualTo("/removed.md");
    }
}
