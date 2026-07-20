using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the concrete Postgres store implementations that back
/// the ingest data model — <see cref="PostgresDocsPublicSourceStore"/>,
/// <see cref="PostgresDocsPublicDocumentStore"/>, and
/// <see cref="PostgresDocsIngestRunStore"/>.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/DocsIngestStoreTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DocsIngestStoreTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    // ═══════════════════════════════════════════════════════════════════
    //  PostgresDocsPublicSourceStore
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Source_CreateThenGetById_RoundTrips()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var (_, source) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var fetched = await store.GetByIdAsync(source.Id, CancellationToken.None);

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.RepoUrl).IsEqualTo(source.RepoUrl);
    }

    [Test]
    public async Task Source_GetByRepoUrl_FindsExactMatch()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var (_, source) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource(s => s.RepoUrl = "https://github.com/example/get-by-url")
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var fetched = await store.GetByRepoUrlAsync(
            "https://github.com/example/get-by-url",
            CancellationToken.None
        );

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.Id).IsEqualTo(source.Id);
    }

    [Test]
    public async Task Source_Update_PersistsMutableFields()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var (_, source) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var updated = new DocsPublicSource
        {
            Id = source.Id,
            Kind = source.Kind,
            RepoUrl = source.RepoUrl,
            Name = "Renamed Source",
            SyncStatus = "idle",
            Status = "quarantined",
            NextSyncAt = DateTimeOffset.UtcNow.AddHours(1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpdateAsync(updated, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var fetched = await store.GetByIdAsync(source.Id, CancellationToken.None);
        await Assert.That(fetched!.Name).IsEqualTo("Renamed Source");
        await Assert.That(fetched.Status).IsEqualTo("quarantined");
        await Assert.That(fetched.NextSyncAt).IsNotNull();
    }

    [Test]
    public async Task Source_ClaimDueForSync_ClaimsOnlyIdleActivePastDueSources()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);
        var future = DateTimeOffset.UtcNow.AddHours(1);

        var (_, due) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        due.SyncStatus = "idle";
        due.Status = "active";
        due.NextSyncAt = past;

        var (_, notYetDue) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        notYetDue.SyncStatus = "idle";
        notYetDue.Status = "active";
        notYetDue.NextSyncAt = future;

        var (_, alreadyRunning) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        alreadyRunning.SyncStatus = "running";
        alreadyRunning.Status = "active";
        alreadyRunning.NextSyncAt = past;

        var (_, quarantined) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        quarantined.SyncStatus = "idle";
        quarantined.Status = "quarantined";
        quarantined.NextSyncAt = past;

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var claimed = await store.ClaimDueForSyncAsync(10, CancellationToken.None);

        await Assert.That(claimed.Select(c => c.Id)).Contains(due.Id);
        await Assert.That(claimed.Select(c => c.Id)).DoesNotContain(notYetDue.Id);
        await Assert.That(claimed.Select(c => c.Id)).DoesNotContain(alreadyRunning.Id);
        await Assert.That(claimed.Select(c => c.Id)).DoesNotContain(quarantined.Id);

        _context.ChangeTracker.Clear();
        var reloaded = await store.GetByIdAsync(due.Id, CancellationToken.None);
        await Assert.That(reloaded!.SyncStatus).IsEqualTo("queued");
        await Assert.That(reloaded.ActiveSyncRunCreatedAtUtc).IsNotNull();
        await AssertPostgresMicrosecondPrecisionAsync(reloaded.ActiveSyncRunCreatedAtUtc.Value);
        await Assert.That(reloaded.SyncQueuedAtUtc).IsNotNull();
        await AssertPostgresMicrosecondPrecisionAsync(reloaded.SyncQueuedAtUtc.Value);
    }

    [Test]
    public async Task Source_ClaimDueForSync_RespectsLimit()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 3; i++)
        {
            var (_, source) = await EntityGraph
                .AddGeneratedSeed(_context)
                .AddDocsPublicSource()
                .BuildAsync();
            source.SyncStatus = "idle";
            source.Status = "active";
            source.NextSyncAt = past;
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var claimed = await store.ClaimDueForSyncAsync(2, CancellationToken.None);

        await Assert.That(claimed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Source_ResetStalledSyncs_StaleAndFreshSources_ClearsOnlyStaleSources()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var now = DateTimeOffset.UtcNow;

        var (_, staleQueued) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        staleQueued.SyncStatus = "queued";
        staleQueued.NextSyncAt = now.AddHours(1);
        staleQueued.ActiveSyncRunId = "run_stale_queued";
        staleQueued.ActiveSyncRunCreatedAtUtc = now.AddHours(-1);
        staleQueued.SyncQueuedAtUtc = now.AddHours(-1);
        _context.DocsIngestRuns.Add(
            new DocsIngestRun
            {
                Id = staleQueued.ActiveSyncRunId,
                CreatedAtUtc = staleQueued.ActiveSyncRunCreatedAtUtc.Value,
                SourceKind = RepositorySourceKind.Public,
                RepoUrl = staleQueued.RepoUrl,
                PublicSourceId = staleQueued.Id,
                Trigger = IngestTriggerReason.Scheduled,
                Status = IngestRunStatus.Running,
                UpdatedAtUtc = staleQueued.ActiveSyncRunCreatedAtUtc.Value,
            }
        );

        var (_, freshRunning) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        freshRunning.SyncStatus = "running";
        freshRunning.ActiveSyncRunId = "run_fresh_running";
        freshRunning.ActiveSyncRunCreatedAtUtc = now;
        freshRunning.SyncStartedAtUtc = now.AddMinutes(-10);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var resets = await store.ResetStalledSyncsAsync(
            now,
            queuedStaleAfter: TimeSpan.FromMinutes(30),
            runningStaleAfter: TimeSpan.FromHours(2),
            limit: 10,
            CancellationToken.None
        );

        await Assert.That(resets.Select(reset => reset.RunId)).Contains("run_stale_queued");
        await Assert.That(resets.Select(reset => reset.RunId)).DoesNotContain("run_fresh_running");

        _context.ChangeTracker.Clear();
        var reloadedStale = await store.GetByIdAsync(staleQueued.Id, CancellationToken.None);
        var reloadedFresh = await store.GetByIdAsync(freshRunning.Id, CancellationToken.None);
        var reloadedRun = await _context.DocsIngestRuns.SingleAsync(run =>
            run.Id == "run_stale_queued"
        );
        await Assert.That(reloadedStale!.SyncStatus).IsEqualTo("idle");
        await Assert.That(reloadedStale.ActiveSyncRunId).IsNull();
        await Assert.That(reloadedStale.NextSyncAt).IsEqualTo(now.TruncateToPostgresPrecision());
        await Assert.That(reloadedRun.Status).IsEqualTo(IngestRunStatus.Stalled);
        await Assert.That(reloadedFresh!.SyncStatus).IsEqualTo("running");
        await Assert.That(reloadedFresh.ActiveSyncRunId).IsEqualTo("run_fresh_running");
    }

    [Test]
    public async Task Source_ResetStalledSyncs_MissingActiveRun_LeavesLeaseIntact()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var now = DateTimeOffset.UtcNow;

        var (_, source) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();
        source.SyncStatus = "queued";
        source.NextSyncAt = now.AddHours(1);
        source.ActiveSyncRunId = "run_missing";
        source.ActiveSyncRunCreatedAtUtc = now.AddHours(-1);
        source.SyncQueuedAtUtc = now.AddHours(-1);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var resets = await store.ResetStalledSyncsAsync(
            now,
            queuedStaleAfter: TimeSpan.FromMinutes(30),
            runningStaleAfter: TimeSpan.FromHours(2),
            limit: 10,
            CancellationToken.None
        );

        await Assert.That(resets).IsEmpty();

        _context.ChangeTracker.Clear();
        var reloaded = await store.GetByIdAsync(source.Id, CancellationToken.None);
        await Assert.That(reloaded!.SyncStatus).IsEqualTo("queued");
        await Assert.That(reloaded.ActiveSyncRunId).IsEqualTo("run_missing");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostgresDocsPublicDocumentStore
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Document_UpsertNewPath_ReturnsAdded()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .BuildAsync();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = "/guides/new.md",
                Title = "New",
                TitleNormalized = "new",
                Content = "# New\n\nBody.",
                ContentHash = "hash_new",
                SyncRunId = "run_1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Added);
        await Assert.That(result.Document.Path).IsEqualTo("/guides/new.md");
    }

    [Test]
    public async Task Document_UpsertSamePathChangedHash_ReturnsUpdated()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/guides/update.md";
                p.ContentHash = "hash_v1";
                p.SyncRunId = "run_1";
            })
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = "/guides/update.md",
                Title = "Updated",
                TitleNormalized = "updated",
                Content = "# Updated\n\nBody.",
                ContentHash = "hash_v2",
                SyncRunId = "run_2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Updated);
        await Assert.That(result.Document.ContentHash).IsEqualTo("hash_v2");
        await Assert.That(result.Document.SyncRunId).IsEqualTo("run_2");
    }

    [Test]
    public async Task Document_UpsertSamePathSameHash_ReturnsUnchangedAndDoesNotAdvanceUpdatedAt()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, docs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/guides/stable.md";
                p.ContentHash = "hash_stable";
                p.SyncRunId = "run_1";
            })
            .BuildAsync();
        var originalUpdatedAt = docs[0].UpdatedAt;
        _context.ChangeTracker.Clear();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = "/guides/stable.md",
                Title = "Stable",
                TitleNormalized = "stable",
                Content = "irrelevant — hash unchanged means content fields are not applied",
                ContentHash = "hash_stable",
                SyncRunId = "run_2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Unchanged);
        await Assert.That(result.Document.SyncRunId).IsEqualTo("run_2");
        await Assert
            .That(result.Document.UpdatedAt.TruncateToPostgresPrecision())
            .IsEqualTo(originalUpdatedAt.TruncateToPostgresPrecision());
    }

    [Test]
    public async Task Document_UpsertSameHashNewPath_ReturnsMovedAndRecordsPreviousPath()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/old-location/guide.md";
                p.ContentHash = "hash_move";
                p.SyncRunId = "run_1";
            })
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = "/new-location/guide.md",
                Title = "Guide",
                TitleNormalized = "guide",
                Content = "# Guide\n\nBody.",
                ContentHash = "hash_move",
                SyncRunId = "run_2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Moved);
        await Assert.That(result.Document.Path).IsEqualTo("/new-location/guide.md");
        await Assert.That(result.Document.PreviousPaths).Contains("/old-location/guide.md");
    }

    [Test]
    public async Task Document_UpsertAmbiguousHashWithinSource_ReturnsAddedNotThrows()
    {
        // Two distinct existing files in the same source legitimately share
        // content (e.g. copied templates). A new path with that same hash is
        // genuinely ambiguous — it must not throw and must not guess which of
        // the two existing rows "moved".
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(
                p =>
                {
                    p.Path = "/templates/a.md";
                    p.ContentHash = "hash_ambiguous";
                    p.SyncRunId = "run_1";
                },
                p =>
                {
                    p.Path = "/templates/b.md";
                    p.ContentHash = "hash_ambiguous";
                    p.SyncRunId = "run_1";
                }
            )
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = "/templates/c.md",
                Title = "Template",
                TitleNormalized = "template",
                Content = "# Template\n\nShared body.",
                ContentHash = "hash_ambiguous",
                SyncRunId = "run_2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Added);
        await Assert.That(result.Document.Path).IsEqualTo("/templates/c.md");

        var allWithHash = await store.ListBySourceAsync(source.Id, CancellationToken.None);
        await Assert.That(allWithHash.Count(d => d.ContentHash == "hash_ambiguous")).IsEqualTo(3);
    }

    [Test]
    public async Task Document_UpsertSameHashDifferentSource_ReturnsAddedNotMoved()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, sourceA, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource(s => s.RepoUrl = "https://github.com/example/isolation-a")
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/guide.md";
                p.ContentHash = "hash_cross_store";
                p.SyncRunId = "run_1";
            })
            .BuildAsync();
        var (_, sourceB) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource(s => s.RepoUrl = "https://github.com/example/isolation-b")
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var result = await store.UpsertAsync(
            new DocsPublicDocument
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = sourceB.Id,
                Path = "/guide.md",
                Title = "Guide",
                TitleNormalized = "guide",
                Content = "# Guide\n\nBody.",
                ContentHash = "hash_cross_store",
                SyncRunId = "run_1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        await Assert.That(result.Kind).IsEqualTo(DocumentUpsertKind.Added);
        await Assert.That(result.Document.PublicSourceId).IsEqualTo(sourceB.Id);
    }

    [Test]
    public async Task Document_DeleteUnstamped_RemovesOnlyRowsFromOtherRunsInThatSource()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, sourceA, docsA) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource(s => s.RepoUrl = "https://github.com/example/sweep-a")
            .AddDocsPublicDocuments(
                p =>
                {
                    p.Path = "/kept.md";
                    p.SyncRunId = "run_current";
                },
                p =>
                {
                    p.Path = "/removed.md";
                    p.SyncRunId = "run_old";
                }
            )
            .BuildAsync();
        var (_, sourceB, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource(s => s.RepoUrl = "https://github.com/example/sweep-b")
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/other-source.md";
                p.SyncRunId = "run_old";
            })
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var deleted = await store.DeleteUnstampedAsync(
            sourceA.Id,
            "run_current",
            CancellationToken.None
        );

        await Assert.That(deleted).IsEqualTo(1);

        var remaining = await store.ListBySourceAsync(sourceA.Id, CancellationToken.None);
        await Assert.That(remaining.Select(d => d.Path)).Contains("/kept.md");
        await Assert.That(remaining.Select(d => d.Path)).DoesNotContain("/removed.md");

        var otherSourceDocs = await store.ListBySourceAsync(sourceB.Id, CancellationToken.None);
        await Assert.That(otherSourceDocs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Document_GetByPath_ExactSuffixAndFileName_AllResolve()
    {
        // Mirrors LibraryDocumentStoreIntegrationTests.GetByPath_ExactSuffixAndFileName_AllResolve
        // — the suffix-match tier added to PostgresDocsPublicDocumentStore to
        // reach parity with the private library-document store.
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, docs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(p => p.Path = "/some/path/doc.md")
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var expected = docs[0];
        var exact = await store.GetByPathAsync(source.Id, "/some/path/doc.md", default);
        var suffix = await store.GetByPathAsync(source.Id, "/path/doc.md", default);
        var fileName = await store.GetByPathAsync(source.Id, "doc.md", default);

        await Assert.That(exact?.Id).IsEqualTo(expected.Id);
        await Assert.That(suffix?.Id).IsEqualTo(expected.Id);
        await Assert.That(fileName?.Id).IsEqualTo(expected.Id);
    }

    [Test]
    public async Task Document_GetByPath_ShortSuffix_ReturnsMostSpecificCandidate()
    {
        var store = new PostgresDocsPublicDocumentStore(_context);
        var (_, source, docs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(
                p => p.Path = "/to-the-file.md",
                p => p.Path = "/some/path/to-the-file.md"
            )
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var shorter = docs[0];
        var longer = docs[1];

        var result = await store.GetByPathAsync(source.Id, "/path/to-the-file.md", default);

        await Assert.That(result?.Id).IsEqualTo(longer.Id);
        await Assert.That(result?.Id).IsNotEqualTo(shorter.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostgresDocsIngestRunStore
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Run_CreateThenGet_RoundTrips()
    {
        var store = new PostgresDocsIngestRunStore(_context);
        var run = new DocsIngestRun
        {
            Id = SeedContext.NewId("run"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceKind = RepositorySourceKind.Private,
            RepoUrl = "https://github.com/acme/run-store",
            OrganizationId = "org_run_store",
            LibraryId = "lib_run_store",
            Trigger = IngestTriggerReason.Manual,
            Status = IngestRunStatus.Running,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.CreateAsync(run, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var fetched = await store.GetAsync(run.Id, run.CreatedAtUtc, CancellationToken.None);

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.RepoUrl).IsEqualTo("https://github.com/acme/run-store");
    }

    [Test]
    public async Task Run_Finalize_SetsCountsStatusAndCompletion()
    {
        var store = new PostgresDocsIngestRunStore(_context);
        var (_, runs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsIngestRuns()
            .BuildAsync();
        var run = runs[0];
        _context.ChangeTracker.Clear();

        var completedAt = DateTimeOffset.UtcNow;
        await store.FinalizeAsync(
            run.Id,
            run.CreatedAtUtc,
            new IngestRunFinalization(
                Status: IngestRunStatus.Partial,
                FilesTotal: 5,
                FilesAdded: 3,
                FilesUpdated: 0,
                FilesMoved: 0,
                FilesSkipped: 0,
                FilesDeleted: 0,
                FilesFailed: 2,
                AuthFailure: false,
                FailureMessage: "2 files failed to parse",
                CompletedAtUtc: completedAt
            ),
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var fetched = await store.GetAsync(run.Id, run.CreatedAtUtc, CancellationToken.None);
        await Assert.That(fetched!.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(fetched.FilesAdded).IsEqualTo(3);
        await Assert.That(fetched.FilesFailed).IsEqualTo(2);
        await Assert.That(fetched.FailureMessage).IsEqualTo("2 files failed to parse");
        await Assert.That(fetched.CompletedAtUtc).IsNotNull();
    }

    [Test]
    public async Task Run_MarkStalled_OnlyUpdatesRunningRun()
    {
        var store = new PostgresDocsIngestRunStore(_context);
        var (_, runs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsIngestRuns(
                p =>
                {
                    p.Id = "run_running";
                    p.Status = IngestRunStatus.Running;
                },
                p =>
                {
                    p.Id = "run_finished";
                    p.Status = IngestRunStatus.Succeeded;
                }
            )
            .BuildAsync();
        var running = runs.Single(run => run.Id == "run_running");
        var finished = runs.Single(run => run.Id == "run_finished");
        _context.ChangeTracker.Clear();

        var completedAt = DateTimeOffset.UtcNow;
        var runningUpdated = await store.MarkStalledAsync(
            running.Id,
            running.CreatedAtUtc,
            completedAt,
            "stalled",
            CancellationToken.None
        );
        var finishedUpdated = await store.MarkStalledAsync(
            finished.Id,
            finished.CreatedAtUtc,
            completedAt,
            "stalled",
            CancellationToken.None
        );

        await Assert.That(runningUpdated).IsTrue();
        await Assert.That(finishedUpdated).IsFalse();

        var reloadedRunning = await store.GetAsync(
            running.Id,
            running.CreatedAtUtc,
            CancellationToken.None
        );
        var reloadedFinished = await store.GetAsync(
            finished.Id,
            finished.CreatedAtUtc,
            CancellationToken.None
        );
        await Assert.That(reloadedRunning!.Status).IsEqualTo(IngestRunStatus.Stalled);
        await Assert
            .That(reloadedRunning.CompletedAtUtc)
            .IsEqualTo(completedAt.TruncateToPostgresPrecision());
        await Assert.That(reloadedFinished!.Status).IsEqualTo(IngestRunStatus.Succeeded);
    }

    [Test]
    public async Task Run_ListByOrganization_ReturnsNewestFirstScopedToOrg()
    {
        var store = new PostgresDocsIngestRunStore(_context);
        var (seedA, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsIngestRuns(
                p =>
                {
                    p.RepoUrl = "https://github.com/acme/list-a-1";
                    p.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
                },
                p =>
                {
                    p.RepoUrl = "https://github.com/acme/list-a-2";
                    p.CreatedAtUtc = DateTimeOffset.UtcNow;
                }
            )
            .BuildAsync();
        await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsIngestRuns(p => p.RepoUrl = "https://github.com/acme/list-b")
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var results = await store.ListByOrganizationAsync(
            seedA.Organization.Id,
            10,
            CancellationToken.None
        );

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].RepoUrl).IsEqualTo("https://github.com/acme/list-a-2");
        await Assert.That(results[1].RepoUrl).IsEqualTo("https://github.com/acme/list-a-1");
    }

    [Test]
    public async Task Run_ListByLibrary_PagesThroughKeysetCursor()
    {
        // Three runs, oldest to newest, all for the same library — proves both
        // the newest-first ordering and that the keyset cursor (created_at_utc,
        // id) correctly resumes into a second page without re-showing or
        // skipping a row, including the string.Compare(...) < 0 id tiebreaker
        // translating correctly against real Postgres.
        var store = new PostgresDocsIngestRunStore(_context);
        const string libraryId = "lib_keyset_paging";
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsIngestRuns(
                p =>
                {
                    p.LibraryId = libraryId;
                    p.RepoUrl = "https://github.com/acme/keyset";
                    p.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
                },
                p =>
                {
                    p.LibraryId = libraryId;
                    p.RepoUrl = "https://github.com/acme/keyset";
                    p.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
                },
                p =>
                {
                    p.LibraryId = libraryId;
                    p.RepoUrl = "https://github.com/acme/keyset";
                    p.CreatedAtUtc = DateTimeOffset.UtcNow;
                }
            )
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var firstPage = await store.ListByLibraryAsync(
            seed.Organization.Id,
            libraryId,
            beforeCreatedAtUtc: null,
            beforeId: null,
            limit: 2,
            CancellationToken.None
        );

        await Assert.That(firstPage.Count).IsEqualTo(2);
        var newest = firstPage[0];
        var middle = firstPage[1];

        var secondPage = await store.ListByLibraryAsync(
            seed.Organization.Id,
            libraryId,
            beforeCreatedAtUtc: middle.CreatedAtUtc,
            beforeId: middle.Id,
            limit: 2,
            CancellationToken.None
        );

        await Assert.That(secondPage.Count).IsEqualTo(1);
        var oldest = secondPage[0];

        // All three ids distinct across pages — no duplicate, no gap.
        await Assert
            .That(new[] { newest.Id, middle.Id, oldest.Id }.Distinct().Count())
            .IsEqualTo(3);
        await Assert.That(oldest.CreatedAtUtc).IsLessThan(middle.CreatedAtUtc);
        await Assert.That(middle.CreatedAtUtc).IsLessThan(newest.CreatedAtUtc);
    }

    private static async Task AssertPostgresMicrosecondPrecisionAsync(DateTimeOffset value) =>
        await Assert.That(value.Ticks % TimeSpan.TicksPerMicrosecond).IsEqualTo(0);
}
