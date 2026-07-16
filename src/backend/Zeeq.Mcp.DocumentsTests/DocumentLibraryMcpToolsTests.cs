using System.Security.Claims;
using System.Text.Json;
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
            TestUser(),
            ""
        );

        await Assert.That(response).IsEqualTo("library is required.");
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

    private static Library TestLibrary() =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            Name = "kb",
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
}
