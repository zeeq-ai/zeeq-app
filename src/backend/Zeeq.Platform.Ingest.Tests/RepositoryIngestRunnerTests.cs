using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="RepositoryIngestRunner"/> — the public-source ingest
/// pipeline: file discovery, upsert branching, per-file failure isolation, the
/// clean-pass-only deletion sweep, and run-record lifecycle.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/RepositoryIngestRunnerTests/*"
/// </summary>
public sealed class RepositoryIngestRunnerTests
{
    private static RepositoryIngestJob PublicJob(
        string publicSourceId = "src_1",
        EffectiveFilter? filter = null
    ) =>
        new()
        {
            RunId = $"run_{Guid.CreateVersion7():N}",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/docs",
            PublicSourceId = publicSourceId,
            Trigger = IngestTriggerReason.Manual,
            Filter = filter ?? EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    [Test]
    public async Task RunAsync_NewFiles_AddsAllAndSucceeds()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("guide.md", "# Guide\n\nBody one.");
        workspace.WriteFile("docs/nested.md", "# Nested\n\nBody two.");

        var job = PublicJob();
        var run = await runner.RunAsync(job, workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesTotal).IsEqualTo(2);
        await Assert.That(run.FilesAdded).IsEqualTo(2);
        await Assert.That(run.FilesFailed).IsEqualTo(0);
        await Assert.That(documentStore.Documents.Count).IsEqualTo(2);
        await Assert.That(documentStore.Documents.Select(d => d.Path)).Contains("/guide.md");
        await Assert.That(documentStore.Documents.Select(d => d.Path)).Contains("/docs/nested.md");
        await Assert.That(documentStore.Documents.All(d => d.SyncRunId == job.RunId)).IsTrue();
    }

    [Test]
    public async Task RunAsync_NonMarkdownFiles_AreIgnored()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("guide.md", "# Guide\n\nBody.");
        workspace.WriteFile("README.txt", "not markdown");
        workspace.WriteFile("image.png", "not markdown either");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        await Assert.That(run.FilesTotal).IsEqualTo(1);
        await Assert.That(documentStore.Documents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_IncludeFilter_OnlyIngestsMatchingPaths()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("guides/keep.md", "# Keep\n\nBody.");
        workspace.WriteFile("internal/skip.md", "# Skip\n\nBody.");

        var job = PublicJob(filter: new EffectiveFilter(["guides/*"], []));
        var run = await runner.RunAsync(job, workspace, CancellationToken.None);

        await Assert.That(run.FilesTotal).IsEqualTo(1);
        await Assert.That(documentStore.Documents.Single().Path).IsEqualTo("/guides/keep.md");
    }

    [Test]
    public async Task RunAsync_UnreadableFile_IsIsolatedAndMarksRunPartial()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();

        // Fails the read for exactly one path — deterministic per-file failure
        // injection, rather than relying on OS-specific filesystem tricks.
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance,
            readFile: (path, ct) =>
                path.EndsWith("bad.md", StringComparison.Ordinal)
                    ? throw new IOException("Simulated unreadable file.")
                    : File.ReadAllTextAsync(path, ct)
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("good.md", "# Good\n\nBody.");
        workspace.WriteFile("bad.md", "# Bad\n\nBody.");

        var job = PublicJob();
        var run = await runner.RunAsync(job, workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesTotal).IsEqualTo(2);
        await Assert.That(run.FilesFailed).IsEqualTo(1);
        await Assert.That(run.FilesAdded).IsEqualTo(1);
        await Assert.That(documentStore.Documents.Single().Path).IsEqualTo("/good.md");
    }

    /// <summary>
    /// Locks in the fix for a live bug (2026-07-11): NormalizePath lower-cases every ingested
    /// path, so two on-disk files whose relative paths differ only by a case-insensitive-equal
    /// separator/casing combination now collide on the same stored identity. Before the fix, the
    /// later file's upsert silently overwrote the earlier file's row via the exact-path match —
    /// no error, no log, one file's content permanently lost. The collision must now be caught
    /// and the later file skipped (counted as failed) instead.
    /// </summary>
    /// <remarks>
    /// Uses a literal backslash in a file name (a valid filename character on Linux/macOS, not a
    /// path separator there) rather than two differently-cased real files, so the collision is
    /// reproducible regardless of whether the test runs on a case-sensitive or case-insensitive
    /// filesystem — two differently-cased files on macOS's default case-insensitive APFS volume
    /// would already collide at the OS level before this code ever runs.
    /// </remarks>
    [Test]
    public async Task RunAsync_CaseFoldCollision_SkipsLaterFileInsteadOfOverwriting()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        // Backslash is a literal filename character on Linux/macOS, not a separator — this
        // creates a file sitting directly at the workspace root (not inside "docs/") whose
        // RelativePath, after NormalizePath's Replace('\\','/'), normalizes to the same
        // "/docs/guide.md" identity as the real "docs/guide.md" file below. EnumerateFilesAsync
        // yields a directory's direct file children before recursing into subdirectories, so this
        // root-level file is always seen first, ahead of the nested "docs/guide.md".
        workspace.WriteFile("docs\\guide.md", "# First-seen guide\n\nRoot-level file.");
        workspace.WriteFile("docs/guide.md", "# Colliding guide\n\nNested file.");

        var job = PublicJob();
        var run = await runner.RunAsync(job, workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesTotal).IsEqualTo(2);
        await Assert.That(run.FilesFailed).IsEqualTo(1);
        await Assert.That(run.FilesAdded).IsEqualTo(1);
        await Assert.That(documentStore.Documents.Count).IsEqualTo(1);
        await Assert.That(documentStore.Documents.Single().Path).IsEqualTo("/docs/guide.md");
        await Assert.That(documentStore.Documents.Single().Content).Contains("Root-level file.");
    }

    [Test]
    public async Task RunAsync_PartialRun_SkipsDeletionSweep()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();

        // Seed a document from a prior run that will be absent from this pass's
        // file tree — normally swept, but must survive because this pass fails
        // one file.
        documentStore.Documents.Add(
            new DocsPublicDocument
            {
                Id = "doc_prior",
                PublicSourceId = "src_1",
                Path = "/stale.md",
                Title = "Stale",
                TitleNormalized = "stale",
                Content = "# Stale\n\nBody.",
                ContentHash = "prior-hash",
                SyncRunId = "run_prior",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            }
        );

        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance,
            readFile: (path, ct) =>
                path.EndsWith("bad.md", StringComparison.Ordinal)
                    ? throw new IOException("Simulated unreadable file.")
                    : File.ReadAllTextAsync(path, ct)
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("good.md", "# Good\n\nBody.");
        workspace.WriteFile("bad.md", "# Bad\n\nBody.");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesDeleted).IsEqualTo(0);
        await Assert.That(documentStore.Documents.Select(d => d.Path)).Contains("/stale.md");
    }

    [Test]
    public async Task RunAsync_CleanPass_SweepsDocumentsAbsentUpstream()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();

        documentStore.Documents.Add(
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

        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("kept.md", "# Kept\n\nBody.");

        var run = await runner.RunAsync(PublicJob(), workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesDeleted).IsEqualTo(1);
        await Assert
            .That(documentStore.Documents.Select(d => d.Path))
            .DoesNotContain("/removed.md");
        await Assert.That(documentStore.Documents.Select(d => d.Path)).Contains("/kept.md");
    }

    [Test]
    public async Task RunAsync_ReRunWithMovedFile_UpsertsAsMoved()
    {
        var documentStore = new FakeDocsPublicDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            documentStore,
            new FakeLibraryDocumentStore(),
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var firstWorkspace = new TempDirectoryWorkspace();
        firstWorkspace.WriteFile("old-location/guide.md", "# Guide\n\nStable body.");
        await runner.RunAsync(PublicJob(), firstWorkspace, CancellationToken.None);

        await using var secondWorkspace = new TempDirectoryWorkspace();
        secondWorkspace.WriteFile("new-location/guide.md", "# Guide\n\nStable body.");
        var run = await runner.RunAsync(PublicJob(), secondWorkspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesMoved).IsEqualTo(1);
        await Assert
            .That(documentStore.Documents.Single().Path)
            .IsEqualTo("/new-location/guide.md");
        await Assert
            .That(documentStore.Documents.Single().PreviousPaths)
            .Contains("/old-location/guide.md");
    }

    private static RepositoryIngestJob PrivateJob(
        string organizationId = "org_1",
        string libraryId = "lib_1",
        EffectiveFilter? filter = null
    ) =>
        new()
        {
            RunId = $"run_{Guid.CreateVersion7():N}",
            RunCreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = RepositorySourceKind.Private,
            RepoUrl = "https://github.com/acme/private-docs",
            OrganizationId = organizationId,
            LibraryId = libraryId,
            Trigger = IngestTriggerReason.Manual,
            Filter = filter ?? EffectiveFilter.Empty,
            TraceContext = new ZeeqTraceContext(null, null),
        };

    [Test]
    public async Task RunAsync_PrivateJob_MissingOrganizationOrLibrary_ThrowsArgumentException()
    {
        var runner = new RepositoryIngestRunner(
            new FakeDocsPublicDocumentStore(),
            new FakeLibraryDocumentStore(),
            new FakeDocsIngestRunStore(),
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();

        var job = PrivateJob() with { OrganizationId = null };

        await Assert
            .That(async () => await runner.RunAsync(job, workspace, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RunAsync_PrivateJob_NewFiles_AddsAllAndSucceeds()
    {
        var libraryDocumentStore = new FakeLibraryDocumentStore();
        var runStore = new FakeDocsIngestRunStore();
        var runner = new RepositoryIngestRunner(
            new FakeDocsPublicDocumentStore(),
            libraryDocumentStore,
            runStore,
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("guide.md", "# Guide\n\nBody one.");
        workspace.WriteFile("docs/nested.md", "# Nested\n\nBody two.");

        var job = PrivateJob();
        var run = await runner.RunAsync(job, workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.OrganizationId).IsEqualTo("org_1");
        await Assert.That(run.LibraryId).IsEqualTo("lib_1");
        await Assert.That(run.FilesTotal).IsEqualTo(2);
        await Assert.That(run.FilesAdded).IsEqualTo(2);
        await Assert.That(libraryDocumentStore.Documents.Count).IsEqualTo(2);
        await Assert
            .That(libraryDocumentStore.Documents.Select(d => d.Path))
            .Contains("/guide.md");
        await Assert
            .That(libraryDocumentStore.Documents.All(d => d.SyncRunId == job.RunId))
            .IsTrue();
        // A private-ingested document must be stamped SourceOrigin so the API
        // maps it to Origin="remote" and the editor renders it read-only —
        // otherwise a user could edit and the next sync would silently
        // overwrite their change with no warning.
        await Assert
            .That(libraryDocumentStore.Documents.All(d => d.SourceOrigin is not null))
            .IsTrue();
        await Assert
            .That(libraryDocumentStore.Documents.First().SourceOrigin!.RepoRef)
            .IsEqualTo(job.RepoUrl);
    }

    [Test]
    public async Task RunAsync_PrivateJob_CleanPass_SweepsDocumentsAbsentUpstream()
    {
        var libraryDocumentStore = new FakeLibraryDocumentStore();
        libraryDocumentStore.Documents.Add(
            new LibraryDocument
            {
                Id = "doc_removed",
                OrganizationId = "org_1",
                LibraryId = "lib_1",
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

        var runner = new RepositoryIngestRunner(
            new FakeDocsPublicDocumentStore(),
            libraryDocumentStore,
            new FakeDocsIngestRunStore(),
            NullLogger<RepositoryIngestRunner>.Instance
        );

        await using var workspace = new TempDirectoryWorkspace();
        workspace.WriteFile("kept.md", "# Kept\n\nBody.");

        var run = await runner.RunAsync(PrivateJob(), workspace, CancellationToken.None);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesDeleted).IsEqualTo(1);
        await Assert
            .That(libraryDocumentStore.Documents.Select(d => d.Path))
            .DoesNotContain("/removed.md");
        await Assert
            .That(libraryDocumentStore.Documents.Select(d => d.Path))
            .Contains("/kept.md");
    }
}
