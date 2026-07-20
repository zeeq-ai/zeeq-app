using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the private snippet store's reconciliation and the document store's
/// snippet-indexing claim/status members.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/SnippetStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SnippetStoreIntegrationTests : PgTransactionalTestBase
{
    private static readonly SnippetIndexingSettings Settings = new();

    public SnippetStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task Replace_InsertsSnippets_WithNullEmbedding()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var composed = new[] { Section("intro-body", ordinal: 0), Code("DoThing();", ordinal: 0) };

        await snippetStore.ReplaceForDocumentAsync(document, composed, default);
        _context.ChangeTracker.Clear();

        var rows = await LoadSnippetsAsync(document);
        await Assert.That(rows).Count().IsEqualTo(2);
        await Assert.That(rows.All(r => r.Embedding is null)).IsTrue();
    }

    [Test]
    public async Task Replace_InsertsSnippets_WithLongMarkdownMetadata()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var composed = Compose(
            SnippetKind.Code,
            "DoThing();",
            ordinal: 0,
            language: new string('l', 96),
            tag: new string('t', 320),
            preceding: "Example:"
        ) with
        {
            Header = new string('h', 1200),
            HeadingPath = new string('p', 2400),
        };

        await snippetStore.ReplaceForDocumentAsync(document, [composed], default);
        _context.ChangeTracker.Clear();

        var row = (await LoadSnippetsAsync(document)).Single();
        await Assert.That(row.Language).IsEqualTo(composed.Language);
        await Assert.That(row.Tag).IsEqualTo(composed.Tag);
        await Assert.That(row.Header).IsEqualTo(composed.Header);
        await Assert.That(row.HeadingPath).IsEqualTo(composed.HeadingPath);
    }

    [Test]
    public async Task Replace_UnchangedHash_KeepsExistingEmbedding()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var composed = new[] { Section("stable-body", ordinal: 0) };

        await snippetStore.ReplaceForDocumentAsync(document, composed, default);
        _context.ChangeTracker.Clear();

        // Simulate the embedding pipeline having filled in a vector + model stamp.
        var stored = (await LoadSnippetsAsync(document)).Single();
        var originalId = stored.Id;
        await _context
            .LibraryDocumentSnippets.Where(s => s.Id == originalId)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(s => s.EmbeddingModel, "test-model@768")
            );
        _context.ChangeTracker.Clear();

        // Re-run reconciliation with the same composed snippet (same hash).
        await snippetStore.ReplaceForDocumentAsync(document, composed, default);
        _context.ChangeTracker.Clear();

        var after = await LoadSnippetsAsync(document);
        await Assert.That(after).Count().IsEqualTo(1);
        await Assert.That(after.Single().Id).IsEqualTo(originalId);
        await Assert.That(after.Single().EmbeddingModel).IsEqualTo("test-model@768");
    }

    /// <summary>
    /// Locks in the fix for a live bug found via manual testing (2026-07-11): the
    /// AddSnippetEmbeddingPayload migration backfilled every pre-existing row's EmbeddingPayload to
    /// an empty string, and because reconciliation only set EmbeddingPayload on newly-inserted rows,
    /// a row matched as "unchanged" by ContentHash kept that empty string forever — silently
    /// starving the embedding pipeline of real content. Reconciliation must now refresh
    /// EmbeddingPayload even on the "unchanged" branch.
    /// </summary>
    [Test]
    public async Task Replace_UnchangedHash_RefreshesStaleEmbeddingPayload()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var composed = Section("stable-body", ordinal: 0);

        await snippetStore.ReplaceForDocumentAsync(document, [composed], default);
        _context.ChangeTracker.Clear();

        // Simulate the migration-backfill bug: blank the payload on an already-reconciled row
        // without changing its ContentHash, exactly as the historical migration did.
        var stored = (await LoadSnippetsAsync(document)).Single();
        var originalId = stored.Id;
        await _context
            .LibraryDocumentSnippets.Where(s => s.Id == originalId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.EmbeddingPayload, ""));
        _context.ChangeTracker.Clear();

        // Re-run reconciliation with the same composed snippet (same hash, same ContentHash match).
        await snippetStore.ReplaceForDocumentAsync(document, [composed], default);
        _context.ChangeTracker.Clear();

        var after = await LoadSnippetsAsync(document);
        await Assert.That(after).Count().IsEqualTo(1);
        await Assert.That(after.Single().Id).IsEqualTo(originalId);
        await Assert.That(after.Single().EmbeddingPayload).IsEqualTo(composed.EmbeddingPayload);
    }

    /// <summary>
    /// Same class of bug as <see cref="Replace_UnchangedHash_RefreshesStaleEmbeddingPayload"/>, for
    /// Identifiers: it is derived from Content by <c>SnippetIdentifierExtractor</c>, not from
    /// ContentHash, so an extractor logic change (e.g. adding a reserved-word blocklist) must still
    /// reach an already-reconciled row whose payload hash is unchanged.
    /// </summary>
    [Test]
    public async Task Replace_UnchangedHash_RefreshesStaleIdentifiers()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var firstPass = Code("public class SomeService {}", ordinal: 0) with
        {
            Identifiers = ["public", "class", "someservice"],
        };

        await snippetStore.ReplaceForDocumentAsync(document, [firstPass], default);
        _context.ChangeTracker.Clear();

        var originalId = (await LoadSnippetsAsync(document)).Single().Id;

        // Re-run reconciliation with the same content (same hash) but identifiers a corrected
        // extractor would now produce — the reserved keywords are gone.
        var secondPass = firstPass with
        {
            Identifiers = ["someservice"],
        };
        await snippetStore.ReplaceForDocumentAsync(document, [secondPass], default);
        _context.ChangeTracker.Clear();

        var after = await LoadSnippetsAsync(document);
        await Assert.That(after).Count().IsEqualTo(1);
        await Assert.That(after.Single().Id).IsEqualTo(originalId);
        await Assert.That(after.Single().Identifiers).IsEquivalentTo(["someservice"]);
    }

    [Test]
    public async Task Replace_ChangedContent_InsertsFreshRowAndDeletesOld()
    {
        var (snippetStore, document) = await SeedDocumentAsync();

        await snippetStore.ReplaceForDocumentAsync(document, [Section("first-body", 0)], default);
        _context.ChangeTracker.Clear();
        var firstId = (await LoadSnippetsAsync(document)).Single().Id;

        await snippetStore.ReplaceForDocumentAsync(document, [Section("second-body", 0)], default);
        _context.ChangeTracker.Clear();

        var after = await LoadSnippetsAsync(document);
        await Assert.That(after).Count().IsEqualTo(1);
        await Assert.That(after.Single().Id).IsNotEqualTo(firstId);
        await Assert.That(after.Single().Content).IsEqualTo("second-body");
    }

    [Test]
    public async Task Replace_DuplicateFences_ReconcileByOrdinal()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        var duplicate = new[] { Code("Same();", ordinal: 0), Code("Same();", ordinal: 1) };

        await snippetStore.ReplaceForDocumentAsync(document, duplicate, default);
        _context.ChangeTracker.Clear();

        var rows = await LoadSnippetsAsync(document);
        await Assert.That(rows).Count().IsEqualTo(2);
        await Assert.That(rows.Select(r => r.Ordinal).OrderBy(o => o)).IsEquivalentTo([0, 1]);
    }

    [Test]
    public async Task Replace_EmptySet_DeletesAllSnippets()
    {
        var (snippetStore, document) = await SeedDocumentAsync();

        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        await snippetStore.ReplaceForDocumentAsync(document, [], default);
        _context.ChangeTracker.Clear();

        var rows = await LoadSnippetsAsync(document);
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task Claim_MarksIndexing_AndSkipsFailed()
    {
        var (documentStore, organizationId, libraryId) = await SeedLibraryAsync();
        var pending = await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/a.md",
            DocumentProcessingStatus.Pending
        );
        await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/b.md",
            DocumentProcessingStatus.Failed
        );
        _context.ChangeTracker.Clear();

        var claimed = await documentStore.ClaimPendingIndexingAsync(
            10,
            TimeSpan.FromMinutes(10),
            default
        );

        await Assert.That(claimed.Select(d => d.Id)).IsEquivalentTo([pending.Id]);
        await Assert
            .That(claimed.Single().ProcessingStatus)
            .IsEqualTo(DocumentProcessingStatus.Indexing);
    }

    [Test]
    public async Task Claim_ReclaimsStaleIndexing()
    {
        var (documentStore, organizationId, libraryId) = await SeedLibraryAsync();
        var stale = await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/stale.md",
            DocumentProcessingStatus.Indexing
        );

        // Backdate updated_at beyond the stale window so the row is reclaimable.
        await _context
            .LibraryDocuments.Where(d => d.Id == stale.Id)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-30))
            );
        _context.ChangeTracker.Clear();

        var claimed = await documentStore.ClaimPendingIndexingAsync(
            10,
            TimeSpan.FromMinutes(10),
            default
        );

        await Assert.That(claimed.Select(d => d.Id)).Contains(stale.Id);
    }

    [Test]
    public async Task Claim_FreshIndexing_IsNotReclaimed()
    {
        var (documentStore, organizationId, libraryId) = await SeedLibraryAsync();
        await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/fresh.md",
            DocumentProcessingStatus.Indexing
        );
        _context.ChangeTracker.Clear();

        var claimed = await documentStore.ClaimPendingIndexingAsync(
            10,
            TimeSpan.FromMinutes(10),
            default
        );

        await Assert.That(claimed).IsEmpty();
    }

    [Test]
    public async Task SetProcessingStatus_UpdatesStatus()
    {
        var (documentStore, organizationId, libraryId) = await SeedLibraryAsync();
        var document = await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/doc.md",
            DocumentProcessingStatus.Indexing
        );
        _context.ChangeTracker.Clear();

        await documentStore.SetProcessingStatusAsync(
            document,
            DocumentProcessingStatus.Indexed,
            default
        );
        _context.ChangeTracker.Clear();

        var reloaded = await _context.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        await Assert.That(reloaded.ProcessingStatus).IsEqualTo(DocumentProcessingStatus.Indexed);
    }

    [Test]
    public async Task DocumentDelete_CascadesSnippets()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        await _context.LibraryDocuments.Where(d => d.Id == document.Id).ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();

        var rows = await LoadSnippetsAsync(document);
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task Rename_LeavesSnippetsUntouched()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body-content", 0)], default);
        _context.ChangeTracker.Clear();

        var before = (await LoadSnippetsAsync(document)).Single();

        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        await store.MoveDocumentAsync(
            document.OrganizationId,
            document.LibraryId,
            document.Path,
            "/renamed.md",
            default
        );
        _context.ChangeTracker.Clear();

        // Renames touch only the document row (path). The snippet FK is by document id, so the
        // snippet — and its embedding, once set — survives a rename untouched: no re-embed.
        var after = await LoadSnippetsAsync(document);
        await Assert.That(after).Count().IsEqualTo(1);
        await Assert.That(after.Single().Id).IsEqualTo(before.Id);
    }

    [Test]
    public async Task ClaimMissingEmbeddings_ReturnsRowsNeedingEmbedding_AndStampsLease()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("body-a", 0), Section("body-b", 1)],
            default
        );
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );

        await Assert.That(claimed).Count().IsEqualTo(2);
        await Assert.That(claimed.Select(c => c.EmbeddingPayload)).Contains("Section:body-a:0");

        var rows = await LoadSnippetsAsync(document);
        await Assert.That(rows.All(r => r.EmbeddingStartedAt is not null)).IsTrue();
    }

    [Test]
    public async Task ClaimMissingEmbeddings_SkipsRowsAlreadyStampedWithCurrentModel()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await snippetStore.SetEmbeddingsAsync(
            [new EmbeddingResult(claimed.Single().Id, SampleVector(0.1f))],
            "model@768",
            default
        );
        _context.ChangeTracker.Clear();

        // Second claim under the SAME model should find nothing — already embedded and current.
        var secondClaim = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await Assert.That(secondClaim).IsEmpty();

        // A model/dimension change makes the same row claimable again (self-healing backfill).
        var thirdClaim = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model-v2@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await Assert.That(thirdClaim).Count().IsEqualTo(1);
    }

    /// <summary>
    /// Locks in the guard added for a live bug found via manual testing (2026-07-11): a blank
    /// EmbeddingPayload was silently claimed and sent to the provider, which returns a
    /// normal-looking vector for empty input (no error), so the corruption went undetected. A row
    /// with a blank payload must now stay unclaimed (FTS-only) instead of ever being embedded.
    /// </summary>
    [Test]
    public async Task ClaimMissingEmbeddings_SkipsRowsWithBlankPayload()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        var stored = (await LoadSnippetsAsync(document)).Single();
        await _context
            .LibraryDocumentSnippets.Where(s => s.Id == stored.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.EmbeddingPayload, ""));
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );

        await Assert.That(claimed).IsEmpty();
    }

    [Test]
    public async Task ClaimMissingEmbeddings_ReclaimsStaleLease()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        // First claim marks the lease as started "now".
        var firstClaim = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await Assert.That(firstClaim).Count().IsEqualTo(1);

        // A short lease window means the same row is immediately stale and reclaimable — simulates
        // a crashed worker that claimed but never wrote back.
        var reclaimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.Zero,
            10,
            default
        );
        await Assert.That(reclaimed).Count().IsEqualTo(1);
        await Assert.That(reclaimed.Single().Id).IsEqualTo(firstClaim.Single().Id);
    }

    [Test]
    public async Task SetEmbeddings_WritesVectorModelAndClearsLease()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await snippetStore.SetEmbeddingsAsync(
            [new EmbeddingResult(claimed.Single().Id, SampleVector(0.3f))],
            "model@768",
            default
        );
        _context.ChangeTracker.Clear();

        var row = (await LoadSnippetsAsync(document)).Single();
        await Assert.That(row.Embedding).IsNotNull();
        await Assert.That(row.EmbeddingModel).IsEqualTo("model@768");
        await Assert.That(row.EmbeddingStartedAt).IsNull();
    }

    [Test]
    public async Task ReleaseEmbeddingClaims_ClearsLeaseWithoutWritingVector()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(document, [Section("body", 0)], default);
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await snippetStore.ReleaseEmbeddingClaimsAsync([claimed.Single().Id], default);
        _context.ChangeTracker.Clear();

        var row = (await LoadSnippetsAsync(document)).Single();
        await Assert.That(row.Embedding).IsNull();
        await Assert.That(row.EmbeddingStartedAt).IsNull();

        // Released rows are immediately reclaimable on the next tick, no lease wait required.
        var reclaimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await Assert.That(reclaimed).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Search_FtsOnly_ReturnsMatch_WhenQueryEmbeddingIsNull()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("distinctive elephant migration patterns", 0)],
            default
        );
        _context.ChangeTracker.Clear();

        var results = await snippetStore.SearchAsync(
            new SnippetSearchQuery(
                OrganizationId: document.OrganizationId,
                LibraryId: document.LibraryId,
                PublicSourceId: null,
                Kind: SnippetKind.Section,
                QueryText: "elephant migration",
                QueryEmbedding: null,
                QueryIdentifiers: [],
                ExcludedDocumentPaths: [],
                Limit: 5
            ),
            default
        );

        await Assert.That(results).Count().IsEqualTo(1);
        await Assert.That(results.Single().VectorRank).IsEqualTo(0);
        await Assert.That(results.Single().TextRank).IsGreaterThan(0);
        await Assert.That(results.Single().DocumentPath).IsEqualTo(document.Path);
    }

    [Test]
    public async Task Search_VectorArm_RanksClosestEmbeddingFirst()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("close match content", 0), Section("far match content", 1)],
            default
        );
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        var closeId = claimed.Single(c => c.EmbeddingPayload == "Section:close match content:0").Id;
        var farId = claimed.Single(c => c.EmbeddingPayload == "Section:far match content:1").Id;

        await snippetStore.SetEmbeddingsAsync(
            [
                new EmbeddingResult(closeId, SampleVector(0.1f)),
                new EmbeddingResult(farId, SampleVector(0.9f)),
            ],
            "model@768",
            default
        );
        _context.ChangeTracker.Clear();

        var results = await snippetStore.SearchAsync(
            new SnippetSearchQuery(
                OrganizationId: document.OrganizationId,
                LibraryId: document.LibraryId,
                PublicSourceId: null,
                Kind: SnippetKind.Section,
                QueryText: "nonmatching query text",
                QueryEmbedding: SampleVector(0.1f),
                QueryIdentifiers: [],
                ExcludedDocumentPaths: [],
                Limit: 5
            ),
            default
        );

        await Assert.That(results).Count().IsEqualTo(2);
        await Assert.That(results[0].SnippetId).IsEqualTo(closeId);
        await Assert.That(results[0].VectorRank).IsEqualTo(1);
    }

    [Test]
    public async Task Search_ExcludesDocumentPath()
    {
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("excluded content marker", 0)],
            default
        );
        _context.ChangeTracker.Clear();

        var results = await snippetStore.SearchAsync(
            new SnippetSearchQuery(
                OrganizationId: document.OrganizationId,
                LibraryId: document.LibraryId,
                PublicSourceId: null,
                Kind: SnippetKind.Section,
                QueryText: "excluded content marker",
                QueryEmbedding: null,
                QueryIdentifiers: [],
                ExcludedDocumentPaths: [document.Path],
                Limit: 5
            ),
            default
        );

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task Search_OnCodeReviewPath_HidesSnippetsOfExcludedDocuments_FtsArm()
    {
        // Guards the review-exclusion invariant in the full-text (degraded) arm: snippets whose
        // owning document is flagged ExcludedFromCodeReviews never surface on the code-review
        // path, while the interactive (unmarked-scope) path still sees them.
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("distinctive runbook marker", 0)],
            default
        );
        await MarkDocumentExcludedAsync(document);
        _context.ChangeTracker.Clear();

        var reviewStore = CreateCodeReviewScopedSnippetStore();

        var reviewResults = await reviewStore.SearchAsync(
            FtsQuery(document, "distinctive runbook marker"),
            default
        );
        var interactiveResults = await snippetStore.SearchAsync(
            FtsQuery(document, "distinctive runbook marker"),
            default
        );

        await Assert.That(reviewResults).IsEmpty();
        // Identity + arm assertions prove the interactive hit is the seeded snippet surfacing via
        // full-text rank, not an incidental row (code review follow-up, 2026-07-15).
        await Assert.That(interactiveResults).Count().IsEqualTo(1);
        await Assert.That(interactiveResults.Single().DocumentId).IsEqualTo(document.Id);
        await Assert.That(interactiveResults.Single().TextRank).IsGreaterThan(0);
    }

    [Test]
    public async Task Search_OnCodeReviewPath_HidesSnippetsOfExcludedDocuments_VectorArm()
    {
        // Same invariant for the hybrid (vector + FTS) query shape: the exclusion predicate is
        // applied inside the vector CTE too, so an embedded snippet of an excluded document
        // cannot ride in on vector rank alone (query text intentionally does not match).
        var (snippetStore, document) = await SeedDocumentAsync();
        await snippetStore.ReplaceForDocumentAsync(
            document,
            [Section("embedded runbook content", 0)],
            default
        );
        _context.ChangeTracker.Clear();

        var claimed = await snippetStore.ClaimMissingEmbeddingsAsync(
            "model@768",
            TimeSpan.FromMinutes(10),
            10,
            default
        );
        await snippetStore.SetEmbeddingsAsync(
            [new EmbeddingResult(claimed.Single().Id, SampleVector(0.1f))],
            "model@768",
            default
        );
        await MarkDocumentExcludedAsync(document);
        _context.ChangeTracker.Clear();

        var reviewStore = CreateCodeReviewScopedSnippetStore();

        var reviewResults = await reviewStore.SearchAsync(
            FtsQuery(document, "nonmatching query text") with
            {
                QueryEmbedding = SampleVector(0.1f),
            },
            default
        );
        var interactiveResults = await snippetStore.SearchAsync(
            FtsQuery(document, "nonmatching query text") with
            {
                QueryEmbedding = SampleVector(0.1f),
            },
            default
        );

        await Assert.That(reviewResults).IsEmpty();
        // Identity + arm assertions prove the interactive hit is the seeded snippet surfacing
        // via the vector arm alone (TextRank == 0 — the query text matches nothing), so the
        // empty review-path result above can only be explained by the exclusion predicate in
        // the vector CTE (code review follow-up, 2026-07-15).
        await Assert.That(interactiveResults).Count().IsEqualTo(1);
        await Assert.That(interactiveResults.Single().DocumentId).IsEqualTo(document.Id);
        await Assert.That(interactiveResults.Single().VectorRank).IsEqualTo(1);
        await Assert.That(interactiveResults.Single().TextRank).IsEqualTo(0);
    }

    /// <summary>Flags the document as review-excluded through the store's own narrow update.</summary>
    private async Task MarkDocumentExcludedAsync(LibraryDocument document)
    {
        var documentStore = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var updated = await documentStore.SetCodeReviewExclusionAsync(
            document.OrganizationId,
            document.LibraryId,
            document.Id,
            excluded: true,
            default
        );

        await Assert.That(updated?.ExcludedFromCodeReviews).IsTrue();
    }

    /// <summary>
    /// Builds a snippet store whose scope is marked as code-review execution — the same marking
    /// <c>CodeReviewAgentExecutor.MarkCodeReviewExecutionScope</c> applies per tool invocation.
    /// </summary>
    private PostgresLibraryDocumentSnippetStore CreateCodeReviewScopedSnippetStore() =>
        new(_context, new DocumentSearchScope { ForCodeReviewExecution = true });

    /// <summary>Builds a text-only section search query against the document's library.</summary>
    private static SnippetSearchQuery FtsQuery(LibraryDocument document, string queryText) =>
        new(
            OrganizationId: document.OrganizationId,
            LibraryId: document.LibraryId,
            PublicSourceId: null,
            Kind: SnippetKind.Section,
            QueryText: queryText,
            QueryEmbedding: null,
            QueryIdentifiers: [],
            ExcludedDocumentPaths: [],
            Limit: 5
        );

    /// <summary>Builds a deterministic 768-dim halfvec for search tests (not a real embedding).</summary>
    private static Pgvector.HalfVector SampleVector(float seed) =>
        new(Enumerable.Range(0, 768).Select(i => (Half)(seed + i * 0.0001f)).ToArray());

    [Test]
    public async Task PublicClaim_MarksIndexing_AndReconciles()
    {
        var (_, source, docs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddDocsPublicSource()
            .AddDocsPublicDocuments(p =>
            {
                p.Path = "/guides/public.md";
            })
            .BuildAsync();
        _context.ChangeTracker.Clear();

        var documentStore = new PostgresDocsPublicDocumentStore(_context);

        var claimed = await documentStore.ClaimPendingIndexingAsync(
            10,
            TimeSpan.FromMinutes(10),
            default
        );
        await Assert.That(claimed.Select(d => d.Id)).Contains(docs[0].Id);
        await Assert
            .That(claimed.Single(d => d.Id == docs[0].Id).ProcessingStatus)
            .IsEqualTo(DocumentProcessingStatus.Indexing);

        var snippetStore = new PostgresPublicDocumentSnippetStore(_context);
        await snippetStore.ReplaceForDocumentAsync(
            claimed.Single(d => d.Id == docs[0].Id),
            [Section("public-body", 0)],
            default
        );
        await documentStore.SetProcessingStatusAsync(
            claimed.Single(d => d.Id == docs[0].Id),
            DocumentProcessingStatus.Indexed,
            default
        );
        _context.ChangeTracker.Clear();

        var snippetCount = await _context
            .PublicDocumentSnippets.Where(s => s.DocumentId == docs[0].Id)
            .CountAsync();
        await Assert.That(snippetCount).IsEqualTo(1);

        var reloaded = await _context.DocsPublicDocuments.SingleAsync(d => d.Id == docs[0].Id);
        await Assert.That(reloaded.ProcessingStatus).IsEqualTo(DocumentProcessingStatus.Indexed);
    }

    // ----- helpers -----

    private ComposedSnippet Section(string body, int ordinal) =>
        Compose(SnippetKind.Section, body, ordinal, language: null, tag: null, preceding: null);

    private ComposedSnippet Code(string content, int ordinal) =>
        Compose(
            SnippetKind.Code,
            content,
            ordinal,
            language: "cs",
            tag: "example",
            preceding: "Example:"
        );

    /// <summary>Builds a composed snippet with a hash over its content + ordinal marker.</summary>
    private static ComposedSnippet Compose(
        SnippetKind kind,
        string content,
        int ordinal,
        string? language,
        string? tag,
        string? preceding
    )
    {
        var payload = $"{kind}:{content}:{ordinal}";
        var hash = Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{kind}:{content}")
                )
            )
            .ToLowerInvariant();

        return new ComposedSnippet(
            Kind: kind,
            Header: "Header",
            HeadingPath: "Guide > Header",
            Language: language,
            Tag: tag,
            PrecedingText: preceding,
            Content: content,
            Identifiers: kind == SnippetKind.Code ? ["dothing"] : [],
            EmbeddingPayload: payload,
            ContentHash: hash,
            Ordinal: ordinal,
            TokenCount: content.Length
        );
    }

    private async Task<(
        PostgresLibraryDocumentSnippetStore SnippetStore,
        LibraryDocument Document
    )> SeedDocumentAsync()
    {
        var (documentStore, organizationId, libraryId) = await SeedLibraryAsync();
        var document = await SeedDocumentRowAsync(
            documentStore,
            organizationId,
            libraryId,
            "/doc.md",
            DocumentProcessingStatus.Pending
        );
        _context.ChangeTracker.Clear();

        return (
            new PostgresLibraryDocumentSnippetStore(_context, new DocumentSearchScope()),
            document
        );
    }

    private async Task<(
        PostgresLibraryDocumentStore Store,
        string OrganizationId,
        string LibraryId
    )> SeedLibraryAsync()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;
        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "docs",
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );

        return (store, seed.Organization.Id, library.Id);
    }

    private static Task<LibraryDocument> SeedDocumentRowAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string libraryId,
        string path,
        DocumentProcessingStatus status
    )
    {
        var now = DateTimeOffset.UtcNow;

        return store.UpsertDocumentAsync(
            new LibraryDocument
            {
                Id = SeedContext.NewId("document"),
                OrganizationId = organizationId,
                LibraryId = libraryId,
                Path = path,
                Title = "Doc",
                TitleNormalized = "doc",
                Keywords = [],
                Headings = [],
                Content = "content",
                ProcessingStatus = status,
                TokenCount = 1,
                ContentHash = SeedContext.NewId("hash"),
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
    }

    private async Task<IReadOnlyList<LibraryDocumentSnippet>> LoadSnippetsAsync(
        LibraryDocument document
    ) =>
        await _context
            .LibraryDocumentSnippets.Where(s =>
                s.OrganizationId == document.OrganizationId
                && s.LibraryId == document.LibraryId
                && s.DocumentId == document.Id
            )
            .ToListAsync();
}
