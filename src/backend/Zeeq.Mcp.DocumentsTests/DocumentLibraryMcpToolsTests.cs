using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Mcp.Documents;

namespace Zeeq.Mcp.Docs.Tests;

public sealed class DocumentLibraryMcpToolsTests
{
    [Test]
    public async Task ListLibraries_WithActiveOrganization_ReturnsLibraries()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());

        var response = await DocumentLibraryMcpTools.ListLibraries(store, TestUser());

        await Assert.That(response).Contains("\"name\": \"kb\"");
        await Assert.That(response).Contains("\"description\": \"Knowledge base\"");
    }

    [Test]
    public async Task ListLibraries_WithoutOrganizationClaim_ReturnsValidationError()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var response = await DocumentLibraryMcpTools.ListLibraries(store, anonymous);

        await Assert.That(response).IsEqualTo("Active organization is required.");
    }

    [Test]
    public async Task ListDocuments_WithMissingLibrary_ReturnsValidationError()
    {
        var response = await DocumentLibraryMcpTools.ListDocuments(
            new TestLibraryDocumentStore(),
            new TestHybridCache(),
            TestUser(),
            ""
        );

        await Assert.That(response).IsEqualTo("library is required.");
    }

    [Test]
    public async Task ListDocuments_WithSharedRootAndBranching_StripsRootAndKeepsBranches()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(PathDocument("root/a/one.md", ["x"]));
        store.Documents.Add(PathDocument("root/b/two.md", []));

        var response = await DocumentLibraryMcpTools.ListDocuments(
            store,
            new TestHybridCache(),
            TestUser(),
            "kb"
        );

        await Assert.That(response).Contains("Path root: zeeq://root");
        await Assert.That(response).Contains("one.md");
        await Assert.That(response).Contains("[1]: x");
        await Assert.That(response).Contains("two.md");
        await Assert.That(response).Contains("[0]:");
    }

    [Test]
    public async Task ListDocuments_WithoutSharedRoot_OmitsPathRootLine()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(PathDocument("alpha/one.md", ["x"]));
        store.Documents.Add(PathDocument("beta/two.md", ["y"]));

        var response = await DocumentLibraryMcpTools.ListDocuments(
            store,
            new TestHybridCache(),
            TestUser(),
            "kb"
        );

        await Assert.That(response).DoesNotContain("Path root:");
        await Assert.That(response).Contains("Join key's full branch");
        await Assert.That(response).Contains("alpha");
        await Assert.That(response).Contains("beta");
    }

    [Test]
    public async Task ListDocuments_WithSingleRootLevelDocument_RendersLeafWithoutDirectory()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(PathDocument("solo.md", []));

        var response = await DocumentLibraryMcpTools.ListDocuments(
            store,
            new TestHybridCache(),
            TestUser(),
            "kb"
        );

        await Assert.That(response).DoesNotContain("Path root:");
        await Assert.That(response).Contains("solo.md");
        await Assert.That(response).Contains("[0]:");
    }

    [Test]
    public async Task ListDocuments_WithSingleDocumentUnderDirectoryChain_StripsChainButKeepsDocument()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(PathDocument("a/b/c.md", ["k1", "k2"]));

        var response = await DocumentLibraryMcpTools.ListDocuments(
            store,
            new TestHybridCache(),
            TestUser(),
            "kb"
        );

        await Assert.That(response).Contains("Path root: zeeq://a/b");
        await Assert.That(response).Contains("c.md");
        await Assert.That(response).Contains("[2]: k1,k2");
    }

    [Test]
    public async Task ListDocuments_WithSecondCall_ReturnsCachedResultDespiteStoreChange()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(PathDocument("one.md", ["x"]));
        var cache = new TestHybridCache();

        var first = await DocumentLibraryMcpTools.ListDocuments(store, cache, TestUser(), "kb");

        // Mutates the store after the first call; a fresh (uncached) call would see this.
        store.Documents.Add(PathDocument("two.md", ["y"]));
        var second = await DocumentLibraryMcpTools.ListDocuments(store, cache, TestUser(), "kb");

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(second).DoesNotContain("two.md");
    }

    [Test]
    public async Task ListDocuments_WithCaseDifferingLibraryNames_DoesNotCollideInCache()
    {
        // Library names are case-sensitive and unique-per-org only up to exact string equality,
        // so "kb" and "KB" can be two different libraries. Keying the cache on the resolved
        // library id (not a lowercased name) must keep their responses independent.
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary(id: "lib_lower", name: "kb"));
        store.Libraries.Add(TestLibrary(id: "lib_upper", name: "KB"));
        store.Documents.Add(PathDocument("one.md", ["x"], libraryId: "lib_lower"));
        store.Documents.Add(PathDocument("two.md", ["y"], libraryId: "lib_upper"));
        var cache = new TestHybridCache();

        var lower = await DocumentLibraryMcpTools.ListDocuments(store, cache, TestUser(), "kb");
        var upper = await DocumentLibraryMcpTools.ListDocuments(store, cache, TestUser(), "KB");

        await Assert.That(lower).Contains("one.md");
        await Assert.That(lower).DoesNotContain("two.md");
        await Assert.That(upper).Contains("two.md");
        await Assert.That(upper).DoesNotContain("one.md");
    }

    [Test]
    public async Task ReadDocumentByPath_WithMatch_ReturnsContent()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument(content: "# Guide\n\nBody"));

        var response = await DocumentLibraryMcpTools.ReadDocumentByPath(
            store,
            TestUser(),
            "kb",
            "guide.md"
        );

        await Assert.That(response).IsEqualTo("# Guide\n\nBody");
    }

    [Test]
    public async Task SearchDocuments_WithLargeLimit_ClampsLimitAndReturnsSummaries()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());

        var response = await DocumentLibraryMcpTools.SearchDocuments(
            store,
            TestUser(),
            "kb",
            "guide",
            limit: 100
        );

        await Assert.That(response).Contains("\"path\": \"/docs/guide.md\"");
        await Assert.That(store.SearchLimit).IsEqualTo(50);
    }

    [Test]
    public async Task SearchDocuments_ReturnsFlatResultWithMatchTypeAndScores()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());

        var response = await DocumentLibraryMcpTools.SearchDocuments(
            store,
            TestUser(),
            "kb",
            "guide"
        );

        using var document = JsonDocument.Parse(response);
        var hit = document.RootElement[0];

        // Document fields and match metadata are flat siblings — not nested under a wrapper — so the
        // shape matches list_documents and stays simple for a consuming agent.
        await Assert.That(hit.GetProperty("path").GetString()).IsEqualTo("/docs/guide.md");
        await Assert.That(hit.GetProperty("title").GetString()).IsEqualTo("Guide");
        await Assert.That(hit.GetProperty("matchType").GetString()).IsEqualTo("Both");
        await Assert.That(hit.GetProperty("fullTextScore").GetDouble()).IsEqualTo(0.9);
        await Assert.That(hit.GetProperty("fuzzyScore").GetDouble()).IsEqualTo(0.7);
        await Assert.That(hit.TryGetProperty("document", out _)).IsFalse();
    }

    private static ClaimsPrincipal TestUser() =>
        new(new ClaimsIdentity([new Claim(AuthClaims.OrganizationId, "org_123")], "test"));

    private static Library TestLibrary(string id = "lib_123", string name = "kb") =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            Name = name,
            Description = "Knowledge base",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static LibraryDocument TestDocument(string content = "Body") =>
        new()
        {
            Id = "doc_123",
            OrganizationId = "org_123",
            LibraryId = "lib_123",
            Path = "/docs/guide.md",
            Title = "Guide",
            TitleNormalized = "guide",
            Keywords = ["docs"],
            Headings = ["Guide"],
            Content = content,
            ProcessingStatus = DocumentProcessingStatus.Pending,
            TokenCount = 7,
            ContentHash = "hash",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    /// <summary>
    /// A minimal document for the <c>list_documents</c> path-folding tests, where only path and
    /// keywords vary; unlike <see cref="TestDocument"/> it lets each test control the path shape
    /// that exercises root-stripping and chain-folding.
    /// </summary>
    private static LibraryDocument PathDocument(
        string path,
        string[] keywords,
        string libraryId = "lib_123"
    ) =>
        new()
        {
            Id = $"{libraryId}:{path}",
            OrganizationId = "org_123",
            LibraryId = libraryId,
            Path = path,
            Title = path,
            TitleNormalized = path,
            Keywords = keywords,
            Headings = [],
            Content = "Body",
            ProcessingStatus = DocumentProcessingStatus.Pending,
            TokenCount = 1,
            ContentHash = "hash",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    // NOTE: A near-duplicate TestLibraryDocumentStore exists in Zeeq.Platform.Documents.Tests.
    // This version uses a simpler suffix-based GetByPathAsync because MCP tool tests pass short
    // paths (e.g. "guide.md") rather than normalised paths. Consolidation into Zeeq.Testing is
    // tracked as a follow-up; it requires updating callers to pass full paths so the Platform
    // normaliser semantics can be shared.
    private sealed class TestLibraryDocumentStore : ILibraryDocumentStore
    {
        public List<Library> Libraries { get; } = [];

        public List<LibraryDocument> Documents { get; } = [];

        public int? SearchLimit { get; private set; }

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
            CancellationToken ct
        ) =>
            Task.FromResult(
                Libraries.FirstOrDefault(library =>
                    library.OrganizationId == organizationId && library.Name == name
                )
            );

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                Libraries.Where(library => library.OrganizationId == organizationId).ToArray()
            );

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                Libraries.Where(library => library.PublicSourceId == publicSourceId).ToArray()
            );

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<LibraryDocument>>(
                MatchingDocuments(organizationId, libraryId)
            );

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        ) =>
            Task.FromResult(
                MatchingDocuments(organizationId, libraryId)
                    .FirstOrDefault(document =>
                        document.Path.EndsWith(input, StringComparison.Ordinal)
                    )
            );

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        )
        {
            SearchLimit = limit;

            var matches = MatchingDocuments(organizationId, libraryId)
                .Take(limit)
                .Select(document => new LibraryDocumentMatch(
                    document,
                    DocumentMatchType.Both,
                    0.9,
                    0.7
                ))
                .ToArray();

            return Task.FromResult<IReadOnlyList<LibraryDocumentMatch>>(matches);
        }

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteLibraryAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library?> GetLibraryByIdAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
            int limit,
            TimeSpan staleAfter,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetProcessingStatusAsync(
            LibraryDocument document,
            DocumentProcessingStatus status,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> UpdateSyncStateAsync(
            string organizationId,
            string libraryId,
            string? syncStatus,
            DateTimeOffset? nextSyncAt,
            DateTimeOffset[] manualTriggerHistory,
            DateTimeOffset? sourceSyncedAt,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> DeleteUnstampedAsync(
            string organizationId,
            string libraryId,
            string currentSyncRunId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument> UpsertDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> MoveDocumentAsync(
            string organizationId,
            string libraryId,
            string fromPath,
            string toPath,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
            string organizationId,
            string libraryId,
            string path,
            bool excluded,
            CancellationToken ct
        ) => throw new NotSupportedException();

        private LibraryDocument[] MatchingDocuments(string organizationId, string libraryId) =>
            Documents
                .Where(document =>
                    document.OrganizationId == organizationId && document.LibraryId == libraryId
                )
                .ToArray();
    }

    /// <summary>
    /// In-memory HybridCache for tests: calls the factory on first access and stores the result.
    /// </summary>
    private sealed class TestHybridCache : HybridCache
    {
        private readonly Dictionary<string, object?> _values = [];

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_values.TryGetValue(key, out var value))
            {
                return (T)value!;
            }

            var created = await factory(state, cancellationToken);
            _values[key] = created;
            return created;
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            _values[key] = value;
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(
            string key,
            CancellationToken cancellationToken = default
        )
        {
            _values.Remove(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(
            string tag,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
