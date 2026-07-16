using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for document library retrieval and search.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/LibraryDocumentStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LibraryDocumentStoreIntegrationTests : PgTransactionalTestBase
{
    public LibraryDocumentStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task Search_TitleMatch_RanksFirst()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var titleMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/alpha-title.md",
            title: "Alpha Launch Guide",
            content: "General release notes."
        );
        var contentMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/content-only.md",
            title: "Release Notes",
            content: "Alpha details live in this body."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "alpha", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([titleMatch.Id, contentMatch.Id]);
    }

    [Test]
    public async Task Search_KeywordMatch_RanksAboveContent()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var keywordMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/keyword.md",
            title: "Search Basics",
            keywords: ["alpha"],
            content: "General release notes."
        );
        var contentMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/content.md",
            title: "Content Basics",
            content: "Alpha appears only in the content body."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "alpha", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([keywordMatch.Id, contentMatch.Id]);
    }

    [Test]
    public async Task Search_HeadingMatch_RanksAboveContent()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var headingMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/heading.md",
            title: "Search Basics",
            headings: ["Alpha Setup"],
            content: "General release notes."
        );
        var contentMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/body.md",
            title: "Body Basics",
            content: "Alpha appears only in the body."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "alpha", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([headingMatch.Id, contentMatch.Id]);
    }

    /// <summary>
    /// Locks in the OR-semantics contract (code review follow-up, 2026-07-11): every
    /// space-separated term is an independent alternative, so a document matching only one of the
    /// query's terms still surfaces — the query is not narrowed to documents matching every term.
    /// </summary>
    [Test]
    public async Task Search_MultipleTerms_UsesOrSemantics()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var matchesFirstTermOnly = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/first.md",
            title: "First Guide",
            content: "This document only discusses retries."
        );
        var matchesSecondTermOnly = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/second.md",
            title: "Second Guide",
            content: "This document only discusses backoff."
        );
        await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/neither.md",
            title: "Neither Guide",
            content: "This document discusses something unrelated."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(
            organizationId,
            library.Id,
            "retries backoff",
            10,
            default
        );

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([matchesFirstTermOnly.Id, matchesSecondTermOnly.Id]);
    }

    [Test]
    public async Task Search_LibraryIsolation_DoesNotCrossLibraries()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var otherLibrary = await CreateLibraryAsync(store, organizationId, "other-library");
        var expected = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/search.md",
            title: "Alpha Guide",
            content: "Visible document."
        );
        await CreateDocumentAsync(
            store,
            organizationId,
            otherLibrary.Id,
            "/guides/search.md",
            title: "Alpha Guide",
            content: "Hidden document."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "alpha", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([expected.Id]);
    }

    [Test]
    public async Task Search_NoResults_ReturnsEmpty()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/search.md",
            title: "Alpha Guide",
            content: "Visible document."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "nomatch", 10, default);

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task Search_TitleTypo_RecoversViaFuzzyMatch()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var expected = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/indexes.md",
            title: "Indexes",
            titleNormalized: "indexes",
            content: "Database guide."
        );
        await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/auth.md",
            title: "Authentication",
            titleNormalized: "authentication",
            content: "Auth guide."
        );
        _context.ChangeTracker.Clear();

        // "idnexes" is a transposition typo of "indexes". It does not stem to anything the
        // full-text query can hit, so only the trigram title signal recovers it.
        var results = await store.SearchAsync(organizationId, library.Id, "idnexes", 10, default);

        var top = results[0];
        await Assert.That(top.Document.Id).IsEqualTo(expected.Id);
        await Assert.That(top.MatchType).IsEqualTo(DocumentMatchType.Fuzzy);
        await Assert.That(top.FuzzyScore).IsGreaterThan(0d);
        await Assert.That(top.FullTextScore).IsEqualTo(0d);
    }

    [Test]
    public async Task Search_FullTextAndFuzzy_RanksBothMatchHighest()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var bothMatch = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/alpha.md",
            title: "Alpha",
            titleNormalized: "alpha",
            content: "Alpha release notes."
        );
        var fullTextOnly = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/notes.md",
            title: "Release Notes",
            titleNormalized: "release notes",
            content: "Alpha appears only in the body."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "alpha", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([bothMatch.Id, fullTextOnly.Id]);
        await Assert.That(results[0].Document.Id).IsEqualTo(bothMatch.Id);
        await Assert.That(results[0].MatchType).IsEqualTo(DocumentMatchType.Both);
        await Assert.That(results[1].MatchType).IsEqualTo(DocumentMatchType.FullText);
    }

    [Test]
    public async Task Search_LibraryIsolation_FuzzyDoesNotCrossLibraries()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var otherLibrary = await CreateLibraryAsync(store, organizationId, "other-library");
        var expected = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/indexes.md",
            title: "Indexes",
            titleNormalized: "indexes",
            content: "Index guide."
        );
        await CreateDocumentAsync(
            store,
            organizationId,
            otherLibrary.Id,
            "/guides/indexes.md",
            title: "Indexes",
            titleNormalized: "indexes",
            content: "Other index guide."
        );
        _context.ChangeTracker.Clear();

        var results = await store.SearchAsync(organizationId, library.Id, "idnexes", 10, default);

        await Assert
            .That(results.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([expected.Id]);
    }

    [Test]
    public async Task GetByPath_ExactSuffixAndFileName_ReturnDocument()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var expected = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/some/path/doc.md",
            title: "Path Guide",
            content: "Path content."
        );
        _context.ChangeTracker.Clear();

        var exact = await store.GetByPathAsync(
            organizationId,
            library.Id,
            "/some/path/doc.md",
            default
        );
        var suffix = await store.GetByPathAsync(
            organizationId,
            library.Id,
            "/path/doc.md",
            default
        );
        var fileName = await store.GetByPathAsync(organizationId, library.Id, "doc.md", default);

        await Assert.That(exact?.Id).IsEqualTo(expected.Id);
        await Assert.That(suffix?.Id).IsEqualTo(expected.Id);
        await Assert.That(fileName?.Id).IsEqualTo(expected.Id);
    }

    [Test]
    public async Task GetByPath_ShortSuffix_ReturnsMostSpecificCandidate()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var shorter = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/to-the-file.md",
            title: "Short",
            content: "Short path."
        );
        var longer = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/some/path/to-the-file.md",
            title: "Long",
            content: "Long path."
        );
        _context.ChangeTracker.Clear();

        var result = await store.GetByPathAsync(
            organizationId,
            library.Id,
            "/path/to-the-file.md",
            default
        );

        await Assert.That(result?.Id).IsEqualTo(longer.Id);
        await Assert.That(result?.Id).IsNotEqualTo(shorter.Id);
    }

    [Test]
    public async Task GetByPath_CasedInput_NormalizedBeforeLookup()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var expected = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/doc.md",
            title: "Path Guide",
            content: "Path content."
        );
        _context.ChangeTracker.Clear();

        var result = await store.GetByPathAsync(organizationId, library.Id, "GUIDES/DOC", default);

        await Assert.That(result?.Id).IsEqualTo(expected.Id);
    }

    [Test]
    public async Task GetByPath_NoMatch_ReturnsNull()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();

        var result = await store.GetByPathAsync(organizationId, library.Id, "missing.md", default);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MoveDocument_AliasSourceToCurrentPath_IsNoOpAndDoesNotGrowAliases()
    {
        // Regression (expert review): renaming a document via an old alias to its *current* live
        // path must be an idempotent no-op and must not append a duplicate entry to PreviousPaths.
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var created = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/a.md",
            title: "Doc",
            content: "Body."
        );

        // First move records "/a.md" as an alias and makes "/b.md" the live path.
        await store.MoveDocumentAsync(organizationId, library.Id, "/a.md", "/b.md", default);
        _context.ChangeTracker.Clear();

        // Move again from the old alias "/a.md" to the current live path "/b.md": a no-op.
        var result = await store.MoveDocumentAsync(
            organizationId,
            library.Id,
            "/a.md",
            "/b.md",
            default
        );

        await Assert.That(result?.Id).IsEqualTo(created.Id);
        await Assert.That(result?.Path).IsEqualTo("/b.md");
        // PreviousPaths still holds only the single original alias; no "/b.md" appended.
        await Assert.That(result?.PreviousPaths).IsEquivalentTo(new[] { "/a.md" });
    }

    [Test]
    public async Task ListLibrariesByPublicSourceIdAsync_ReturnsSubscribersAcrossOrganizations()
    {
        // Deliberately cross-org: a public source's subscribers span every
        // organization that linked a library to it, not just one — the
        // union-filter computation (PublicRepositorySyncRequestedHandler)
        // depends on seeing all of them, not an org-scoped subset.
        var seedA = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var seedB = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var libraryA = await store.CreateLibraryAsync(
            LibraryBuilder
                .ForPublicSource("src_shared")
                .Build(SeedContext.NewId("library"), seedA.Organization.Id, "docs-a"),
            default
        );
        var libraryB = await store.CreateLibraryAsync(
            LibraryBuilder
                .ForPublicSource("src_shared")
                .Build(SeedContext.NewId("library"), seedB.Organization.Id, "docs-b"),
            default
        );
        var unrelated = await store.CreateLibraryAsync(
            LibraryBuilder
                .ForPublicSource("src_other")
                .Build(SeedContext.NewId("library"), seedA.Organization.Id, "docs-other"),
            default
        );
        _context.ChangeTracker.Clear();

        var subscribers = await store.ListLibrariesByPublicSourceIdAsync(
            "src_shared",
            CancellationToken.None
        );

        await Assert.That(subscribers.Select(l => l.Id)).IsEquivalentTo([libraryA.Id, libraryB.Id]);
        await Assert.That(subscribers.Select(l => l.Id)).DoesNotContain(unrelated.Id);
    }

    [Test]
    public async Task Search_OnCodeReviewPath_HidesExcludedDocuments()
    {
        // Guards the review-exclusion invariant: a document flagged ExcludedFromCodeReviews never
        // surfaces from search on the code-review execution path, but stays visible to the
        // interactive (unmarked-scope) path.
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var visible = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/visible.md",
            title: "Alpha Guide",
            content: "Alpha implementation guidance."
        );
        var excluded = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/runbook.md",
            title: "Alpha Operations Runbook",
            content: "Alpha operational steps."
        );
        await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            excluded.Id,
            excluded: true,
            default
        );
        _context.ChangeTracker.Clear();

        var reviewStore = CreateCodeReviewScopedStore();

        var reviewResults = await reviewStore.SearchAsync(
            organizationId,
            library.Id,
            "alpha",
            10,
            default
        );
        var interactiveResults = await store.SearchAsync(
            organizationId,
            library.Id,
            "alpha",
            10,
            default
        );

        await Assert
            .That(reviewResults.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([visible.Id]);
        await Assert
            .That(interactiveResults.Select(match => match.Document.Id).ToArray())
            .IsEquivalentTo([visible.Id, excluded.Id]);
    }

    [Test]
    public async Task ListDocuments_OnCodeReviewPath_HidesExcludedDocuments()
    {
        // Guards the listing side of the invariant: excluded documents disappear from
        // list_documents on the review path (they must not be consulted at all) while the
        // interactive path still lists them.
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var visible = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/visible.md",
            title: "Coding Guide",
            content: "Guidance."
        );
        var excluded = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/runbook.md",
            title: "Operations Runbook",
            content: "Operational steps."
        );
        await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            excluded.Id,
            excluded: true,
            default
        );
        _context.ChangeTracker.Clear();

        var reviewStore = CreateCodeReviewScopedStore();

        var reviewListing = await reviewStore.ListDocumentsAsync(
            organizationId,
            library.Id,
            default
        );
        var interactiveListing = await store.ListDocumentsAsync(
            organizationId,
            library.Id,
            default
        );

        await Assert.That(reviewListing.Select(d => d.Id).ToArray()).IsEquivalentTo([visible.Id]);
        await Assert
            .That(interactiveListing.Select(d => d.Id).ToArray())
            .IsEquivalentTo([visible.Id, excluded.Id]);
    }

    [Test]
    public async Task GetByPath_OnCodeReviewPath_StillResolvesExcludedDocuments()
    {
        // Locks the read-by-path contract: even on the review path, an excluded document must
        // resolve when requested directly (read_document_by_path is exempt from the filter).
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var excluded = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/runbook.md",
            title: "Operations Runbook",
            content: "Operational steps."
        );
        await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            excluded.Id,
            excluded: true,
            default
        );
        _context.ChangeTracker.Clear();

        var reviewStore = CreateCodeReviewScopedStore();

        var resolved = await reviewStore.GetByPathAsync(
            organizationId,
            library.Id,
            excluded.Path,
            default
        );

        await Assert.That(resolved?.Id).IsEqualTo(excluded.Id);
        await Assert.That(resolved?.ExcludedFromCodeReviews).IsTrue();
    }

    [Test]
    public async Task SetCodeReviewExclusion_TogglesFlagAndPersists()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var document = await CreateDocumentAsync(
            store,
            organizationId,
            library.Id,
            "/guides/runbook.md",
            title: "Operations Runbook",
            content: "Operational steps."
        );
        _context.ChangeTracker.Clear();

        var marked = await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            document.Id,
            excluded: true,
            default
        );
        _context.ChangeTracker.Clear();
        var reloadedMarked = await store.GetByPathAsync(
            organizationId,
            library.Id,
            document.Path,
            default
        );

        await Assert.That(marked?.ExcludedFromCodeReviews).IsTrue();
        await Assert.That(reloadedMarked?.ExcludedFromCodeReviews).IsTrue();

        var cleared = await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            document.Id,
            excluded: false,
            default
        );
        _context.ChangeTracker.Clear();
        var reloadedCleared = await store.GetByPathAsync(
            organizationId,
            library.Id,
            document.Path,
            default
        );

        await Assert.That(cleared?.ExcludedFromCodeReviews).IsFalse();
        await Assert.That(reloadedCleared?.ExcludedFromCodeReviews).IsFalse();
    }

    [Test]
    public async Task SetCodeReviewExclusion_UnknownDocumentId_ReturnsNull()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();

        var result = await store.SetCodeReviewExclusionAsync(
            organizationId,
            library.Id,
            "missing-doc-id",
            excluded: true,
            default
        );

        await Assert.That(result).IsNull();
    }

    /// <summary>
    /// Builds a store whose scope is marked as code-review execution — the same marking
    /// <c>CodeReviewAgentExecutor.MarkCodeReviewExecutionScope</c> applies per tool invocation.
    /// </summary>
    private PostgresLibraryDocumentStore CreateCodeReviewScopedStore() =>
        new(_context, new DocumentSearchScope { ForCodeReviewExecution = true });

    private async Task<(
        PostgresLibraryDocumentStore Store,
        string OrganizationId,
        Library Library
    )> CreateStoreWithLibraryAsync()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var library = await CreateLibraryAsync(store, seed.Organization.Id, "docs");

        return (store, seed.Organization.Id, library);
    }

    private static Task<Library> CreateLibraryAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string name
    )
    {
        var now = DateTimeOffset.UtcNow;
        return store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = organizationId,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
    }

    private static Task<LibraryDocument> CreateDocumentAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string libraryId,
        string path,
        string title,
        string content,
        string? titleNormalized = null,
        string[]? keywords = null,
        string[]? headings = null
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
                Title = title,
                TitleNormalized = titleNormalized ?? title.ToLowerInvariant(),
                Keywords = keywords ?? [],
                Headings = headings ?? [],
                Content = content,
                ProcessingStatus = DocumentProcessingStatus.Pending,
                TokenCount = content.Length,
                ContentHash = SeedContext.NewId("hash"),
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
    }
}
