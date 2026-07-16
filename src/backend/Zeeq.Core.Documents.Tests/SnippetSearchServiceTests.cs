using Zeeq.Core.Common;
using Zeeq.Core.Documents.Snippets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Zeeq.Core.Documents.Tests;

public sealed class SnippetSearchServiceTests
{
    [Test]
    public async Task SearchAsync_WithBlankLibraryName_ReturnsValidationError()
    {
        var service = BuildService(out _, out _, out _);

        var outcome = await service.SearchAsync(Request(libraryName: ""), CancellationToken.None);

        await Assert.That(outcome.Error).IsEqualTo("library is required.");
        await Assert.That(outcome.Result).IsNull();
    }

    [Test]
    public async Task SearchAsync_WithUnknownLibraryName_ReturnsNotFoundError()
    {
        var service = BuildService(out _, out _, out _);

        var outcome = await service.SearchAsync(
            Request(libraryName: "does-not-exist"),
            CancellationToken.None
        );

        await Assert
            .That(outcome.Error)
            .IsEqualTo(
                "Library 'does-not-exist' was not found; use the list_libraries tool to get valid libraries."
            );
    }

    [Test]
    public async Task SearchAsync_WithBlankQuery_ReturnsValidationError()
    {
        var service = BuildService(out var libraries, out _, out _);
        libraries.Libraries.Add(PrivateLibrary());

        var outcome = await service.SearchAsync(Request(query: "  "), CancellationToken.None);

        await Assert.That(outcome.Error).IsEqualTo("query is required.");
    }

    [Test]
    public async Task SearchAsync_PrivateLibrary_DispatchesToPrivateStoreWithLibraryScope()
    {
        var service = BuildService(out var libraries, out var privateStore, out var publicStore);
        libraries.Libraries.Add(PrivateLibrary());
        privateStore.Rows.Add(SampleRow());

        var outcome = await service.SearchAsync(Request(), CancellationToken.None);

        await Assert.That(outcome.Error).IsNull();
        await Assert.That(outcome.Result!.LibraryName).IsEqualTo("kb");
        await Assert.That(outcome.Result.Rows).Count().IsEqualTo(1);
        await Assert.That(privateStore.LastQuery!.OrganizationId).IsEqualTo("org_123");
        await Assert.That(privateStore.LastQuery.LibraryId).IsEqualTo("lib_123");
        await Assert.That(privateStore.LastQuery.PublicSourceId).IsNull();
        await Assert.That(publicStore.LastQuery).IsNull();
    }

    [Test]
    public async Task SearchAsync_PublicLibrary_DispatchesToPublicStoreWithSourceScope()
    {
        var service = BuildService(out var libraries, out var privateStore, out var publicStore);
        libraries.Libraries.Add(PublicLibrary());
        publicStore.Rows.Add(SampleRow());

        var outcome = await service.SearchAsync(Request(), CancellationToken.None);

        await Assert.That(outcome.Error).IsNull();
        await Assert.That(publicStore.LastQuery!.PublicSourceId).IsEqualTo("pub_source_123");
        await Assert.That(publicStore.LastQuery.OrganizationId).IsNull();
        await Assert.That(publicStore.LastQuery.LibraryId).IsNull();
        await Assert.That(privateStore.LastQuery).IsNull();
    }

    [Test]
    public async Task SearchAsync_ClampsMaxResultsAboveCap()
    {
        var service = BuildService(out var libraries, out var privateStore, out _);
        libraries.Libraries.Add(PrivateLibrary());

        await service.SearchAsync(Request(maxResults: 999), CancellationToken.None);

        await Assert.That(privateStore.LastQuery!.Limit).IsEqualTo(15);
    }

    [Test]
    public async Task SearchAsync_DropsBlankAndMalformedExcludedPaths()
    {
        var service = BuildService(out var libraries, out var privateStore, out _);
        libraries.Libraries.Add(PrivateLibrary());

        await service.SearchAsync(
            Request(excludedDocumentPaths: ["/docs/guide.md", "  ", ""]),
            CancellationToken.None
        );

        await Assert
            .That(privateStore.LastQuery!.ExcludedDocumentPaths)
            .IsEquivalentTo(["/docs/guide.md"]);
    }

    [Test]
    public async Task SearchAsync_WhenEmbeddingGeneratorThrows_DegradesToFullTextOnly()
    {
        var generator = new ThrowingEmbeddingGenerator();
        var service = BuildService(out var libraries, out var privateStore, out _, generator);
        libraries.Libraries.Add(PrivateLibrary());

        var outcome = await service.SearchAsync(Request(), CancellationToken.None);

        await Assert.That(outcome.Error).IsNull();
        await Assert.That(outcome.Result!.Degraded).IsTrue();
        await Assert.That(privateStore.LastQuery!.QueryEmbedding).IsNull();
    }

    [Test]
    public async Task SearchAsync_WhenEmbeddingSucceeds_PassesEmbeddingToStoreAndIsNotDegraded()
    {
        var service = BuildService(out var libraries, out var privateStore, out _);
        libraries.Libraries.Add(PrivateLibrary());

        var outcome = await service.SearchAsync(Request(), CancellationToken.None);

        await Assert.That(outcome.Error).IsNull();
        await Assert.That(outcome.Result!.Degraded).IsFalse();
        await Assert.That(privateStore.LastQuery!.QueryEmbedding).IsNotNull();
    }

    /// <summary>
    /// Closes the empirical gap code review flagged (2026-07-11): a genuine caller cancellation
    /// must propagate, not be swallowed into a degraded result — see the NOTE on
    /// <c>SnippetSearchService.TryEmbedQueryAsync</c>'s catch block.
    /// </summary>
    [Test]
    public async Task SearchAsync_WhenCallerCancels_PropagatesCancellationInsteadOfDegrading()
    {
        var service = BuildService(out var libraries, out _, out _);
        libraries.Libraries.Add(PrivateLibrary());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert
            .That(async () => await service.SearchAsync(Request(), cts.Token))
            .Throws<OperationCanceledException>();
    }

    private static SnippetSearchRequest Request(
        string libraryName = "kb",
        string query = "how do I configure retries",
        int? maxResults = null,
        string[]? excludedDocumentPaths = null
    ) =>
        new(
            OrganizationId: "org_123",
            LibraryName: libraryName,
            Kind: SnippetKind.Section,
            Query: query,
            ExcludedDocumentPaths: excludedDocumentPaths,
            MaxResults: maxResults
        );

    private static Library PrivateLibrary() =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            Name = "kb",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static Library PublicLibrary() =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            Name = "kb",
            PublicSourceId = "pub_source_123",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static SnippetSearchRow SampleRow() =>
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

    private static SnippetSearchService BuildService(
        out FakeLibraryDocumentStore libraries,
        out FakeSnippetStore privateStore,
        out FakeSnippetStore publicStore,
        IEmbeddingGenerator<string, Embedding<float>>? generator = null
    )
    {
        libraries = new FakeLibraryDocumentStore();
        privateStore = new FakeSnippetStore();
        publicStore = new FakeSnippetStore();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        return new SnippetSearchService(
            libraries,
            privateStore,
            publicStore,
            generator ?? new FakeEmbeddingGenerator(),
            new LlmEmbeddingSettings(),
            cache,
            NullLogger<SnippetSearchService>.Instance
        );
    }

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

    private sealed class ThrowingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Provider unavailable.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

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

    /// <summary>
    /// Generic fake usable as both <see cref="ISnippetStore{TDocument}"/> closures (private and
    /// public) — the search tests only ever exercise <see cref="SearchAsync"/>.
    /// </summary>
    /// <remarks>
    /// NOTE: implementing both closed generics on one class was flagged by code review
    /// (2026-07-11) as a compile-time contract risk if the two closures ever diverge beyond
    /// <c>ReplaceForDocumentAsync</c>. Empirically verified, not just asserted: this class compiles
    /// against the real <c>ISnippetStore&lt;TDocument&gt;</c> definition, and
    /// <c>SearchAsync_PrivateLibrary_DispatchesToPrivateStoreWithLibraryScope</c> /
    /// <c>SearchAsync_PublicLibrary_DispatchesToPublicStoreWithSourceScope</c> assert the private
    /// and public instances are dispatched to independently — that would not pass if either
    /// interface's <c>SearchAsync</c> member were silently unsatisfied.
    /// </remarks>
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
