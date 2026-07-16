using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Documents.Tests;

public sealed class SnippetEndpointHandlerTests
{
    [Test]
    public async Task Search_UnknownKind_Returns400()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "paragraph",
            "retries",
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<DocumentError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Message).Contains("kind must be");
    }

    [Test]
    public async Task Search_UnknownLibrary_Returns404()
    {
        var handler = BuildHandler(out _, out _);

        var result = await handler.HandleAsync(
            "org_123",
            "does-not-exist",
            "section",
            "retries",
            null,
            null,
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task Search_BlankQuery_Returns400()
    {
        var handler = BuildHandler(out var libraries, out _);
        libraries.Libraries.Add(TestLibrary());

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "section",
            "  ",
            null,
            null,
            CancellationToken.None
        );

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    [Test]
    public async Task Search_WithMatch_MapsScoreComponentsAndOmitsLibrary()
    {
        var handler = BuildHandler(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "section",
            "retries",
            null,
            null,
            CancellationToken.None
        );

        var ok = result.Result as Ok<SnippetSearchResultResponse[]>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!).HasSingleItem();

        var row = ok.Value![0];

        await Assert.That(row.DocumentPath).IsEqualTo("/docs/guide.md");
        await Assert.That(row.DocumentTitle).IsEqualTo("Guide");
        await Assert.That(row.HeadingPath).IsEqualTo("Guide > Retries");
        await Assert.That(row.Score).IsEqualTo(0.5);
        await Assert.That(row.VectorRank).IsEqualTo(1);
        await Assert.That(row.TextRank).IsEqualTo(1);
        await Assert.That(row.IdentifierMatch).IsFalse();
        await Assert.That(row.Degraded).IsFalse();
    }

    [Test]
    public async Task Search_KindIsCaseInsensitive()
    {
        var handler = BuildHandler(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "SECTION",
            "retries",
            null,
            null,
            CancellationToken.None
        );

        await Assert.That(result.Result is Ok<SnippetSearchResultResponse[]>).IsTrue();
        await Assert.That(snippetStore.LastQuery!.Kind).IsEqualTo(SnippetKind.Section);
    }

    private static SearchSnippetsHandler BuildHandler(
        out FakeLibraryDocumentStore libraries,
        out FakeSnippetStore snippetStore
    )
    {
        libraries = new FakeLibraryDocumentStore();
        snippetStore = new FakeSnippetStore();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        var searchService = new SnippetSearchService(
            libraries,
            snippetStore,
            new FakeSnippetStore(),
            new FakeEmbeddingGenerator(),
            new LlmEmbeddingSettings(),
            cache,
            NullLogger<SnippetSearchService>.Instance
        );

        return new SearchSnippetsHandler(searchService);
    }

    private static Library TestLibrary() =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            Name = "kb",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static SnippetSearchRow SectionRow() =>
        new(
            SnippetId: "snip_1",
            DocumentId: "doc_1",
            DocumentPath: "/docs/guide.md",
            DocumentTitle: "Guide",
            Header: "Retries",
            HeadingPath: "Guide > Retries",
            Language: null,
            Tag: null,
            Content: "Use exponential backoff.",
            TokenCount: 5,
            Score: 0.5,
            VectorRank: 1,
            TextRank: 1,
            IdentifierMatch: false
        );

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var dimensions = options?.Dimensions ?? 8;

            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                    values.Select(_ => new Embedding<float>(new float[dimensions]))
                )
            );
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // NOTE: near-duplicate of the fakes in SnippetSearchServiceTests.cs and
    // DocumentLibraryMcpToolsSearchSnippetsTests.cs — same established precedent as the product
    // code's public/private snippet store duplication; this test only needs GetLibraryAsync and
    // SearchAsync.
    private sealed class FakeLibraryDocumentStore : ILibraryDocumentStore
    {
        public List<Library> Libraries { get; } = [];

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

        public Task<Library?> GetLibraryByIdAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
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

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteLibraryAsync(
            string organizationId,
            string libraryId,
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

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
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
    }

    private sealed class FakeSnippetStore
        : ISnippetStore<LibraryDocument>,
            ISnippetStore<DocsPublicDocument>
    {
        public List<SnippetSearchRow> Rows { get; } = [];

        public SnippetSearchQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(
            SnippetSearchQuery query,
            CancellationToken ct
        )
        {
            LastQuery = query;

            return Task.FromResult<IReadOnlyList<SnippetSearchRow>>(
                Rows.Take(query.Limit).ToArray()
            );
        }

        public Task ReplaceForDocumentAsync(
            LibraryDocument document,
            IReadOnlyList<ComposedSnippet> snippets,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task ReplaceForDocumentAsync(
            DocsPublicDocument document,
            IReadOnlyList<ComposedSnippet> snippets,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<EmbeddingClaim>> ClaimMissingEmbeddingsAsync(
            string embeddingModel,
            TimeSpan lease,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetEmbeddingsAsync(
            IReadOnlyList<EmbeddingResult> results,
            string embeddingModel,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task ReleaseEmbeddingClaimsAsync(
            IReadOnlyList<string> snippetIds,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
