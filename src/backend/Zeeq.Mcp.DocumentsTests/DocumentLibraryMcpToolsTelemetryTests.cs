using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Identity;
using Zeeq.Mcp.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Mcp.Docs.Tests;

/// <summary>
/// Tests that the document MCP tools emit best-effort knowledge-source telemetry — one source per
/// surfaced row (snippet + owning document), missed queries on zero results, and reads — while a
/// <see cref="ToolTelemetrySink" /> scope is active, and stay silent no-ops otherwise.
///
/// dotnet run --project src/backend/Zeeq.Mcp.DocumentsTests --output detailed --disable-logo --treenode-filter "/*/*/DocumentLibraryMcpToolsTelemetryTests/*"
/// </summary>
public sealed class DocumentLibraryMcpToolsTelemetryTests
{
    [Test]
    public async Task SearchSections_WhenScopeActive_RecordsSectionSourceWithDocumentAndHeading()
    {
        var service = BuildSnippetService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchSections(service, TestUser(), "kb", "retries");
        }

        var source = sink.Sources.Single();
        await Assert.That(source.ToolName).IsEqualTo("search_sections");
        await Assert.That(source.Kind).IsEqualTo(ToolKnowledgeSourceKind.Section);
        await Assert.That(source.Usage).IsEqualTo(ToolKnowledgeSourceUsage.Searched);
        await Assert.That(source.Library).IsEqualTo("kb");
        await Assert.That(source.DocumentPath).IsEqualTo("/docs/guide.md");
        await Assert.That(source.DocumentTitle).IsEqualTo("Guide");
        await Assert.That(source.Heading).IsEqualTo("Guide > Retries");
        await Assert.That(source.Language).IsNull();
        await Assert.That(source.Query).IsEqualTo("retries");
        await Assert.That(sink.Misses).IsEmpty();
    }

    [Test]
    public async Task SearchCodeSnippets_RecordsCodeSampleKindWithLanguage()
    {
        var service = BuildSnippetService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(CodeRow());

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchCodeSnippets(
                service,
                TestUser(),
                "kb",
                "retry policy"
            );
        }

        var source = sink.Sources.Single();
        await Assert.That(source.ToolName).IsEqualTo("search_code_snippets");
        await Assert.That(source.Kind).IsEqualTo(ToolKnowledgeSourceKind.CodeSample);
        await Assert.That(source.Language).IsEqualTo("csharp");
        await Assert.That(source.Heading).IsEqualTo("Guide > Retry Policy");
    }

    [Test]
    public async Task SearchSections_WithRows_RecordsRankScoreAndStableIds()
    {
        var service = BuildSnippetService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());

        // Vector-arm miss (0), full-text rank 3 → BestArmRank should pick the non-zero 3.
        snippetStore.Rows.Add(
            SectionRow() with
            {
                SnippetId = "snip_rank",
                Score = 0.0312,
                VectorRank = 0,
                TextRank = 3,
                IdentifierMatch = true,
            }
        );

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchSections(service, TestUser(), "kb", "retries");
        }

        var source = sink.Sources.Single();
        await Assert.That(source.DocumentId).IsEqualTo("doc_1");
        await Assert.That(source.SnippetId).IsEqualTo("snip_rank");
        await Assert.That(source.Rank).IsEqualTo(3);
        await Assert.That(source.Score).IsEqualTo(0.0312);
        await Assert.That(source.IdentifierMatch).IsTrue();
    }

    [Test]
    public async Task SearchSections_ZeroResults_RecordsMissedQueryNotSource()
    {
        var service = BuildSnippetService(out var libraries, out _);
        libraries.Libraries.Add(TestLibrary());

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchSections(
                service,
                TestUser(),
                "kb",
                "nothing matches"
            );
        }

        await Assert.That(sink.Sources).IsEmpty();
        var miss = sink.Misses.Single();
        await Assert.That(miss.Tool).IsEqualTo("search_sections");
        await Assert.That(miss.Query).IsEqualTo("nothing matches");
    }

    [Test]
    public async Task SearchSections_WhenNoScope_DoesNotThrowAndReturnsNormally()
    {
        var service = BuildSnippetService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        // No BeginScope: the sink is a no-op and the tool must behave exactly as before.
        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "kb",
            "retries"
        );

        await Assert.That(response).Contains("# Results for: \"retries\"");
        await Assert.That(ToolTelemetrySink.Current).IsNull();
    }

    [Test]
    public async Task ReadDocumentByPath_OnSuccess_RecordsDocumentReadNoQuery()
    {
        var store = new TestLibraryStore();
        store.Libraries.Add(TestLibrary());
        store.Documents["/docs/guide.md"] = TestDocument();

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.ReadDocumentByPath(
                store,
                TestUser(),
                "kb",
                "/docs/guide.md"
            );
        }

        var source = sink.Sources.Single();
        await Assert.That(source.ToolName).IsEqualTo("read_document_by_path");
        await Assert.That(source.Kind).IsEqualTo(ToolKnowledgeSourceKind.Document);
        await Assert.That(source.Usage).IsEqualTo(ToolKnowledgeSourceUsage.Read);
        await Assert.That(source.Library).IsEqualTo("kb");
        await Assert.That(source.DocumentId).IsEqualTo("doc_1");
        await Assert.That(source.DocumentPath).IsEqualTo("/docs/guide.md");
        await Assert.That(source.DocumentTitle).IsEqualTo("Guide");
        await Assert.That(source.Query).IsNull();
        await Assert.That(source.Heading).IsNull();
        await Assert.That(source.Rank).IsEqualTo(0);
    }

    [Test]
    public async Task ReadDocumentByPath_WhenNotFound_RecordsNothing()
    {
        var store = new TestLibraryStore();
        store.Libraries.Add(TestLibrary());

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.ReadDocumentByPath(
                store,
                TestUser(),
                "kb",
                "/docs/missing.md"
            );
        }

        await Assert.That(sink.Sources).IsEmpty();
        await Assert.That(sink.Misses).IsEmpty();
    }

    [Test]
    public async Task SearchDocuments_RecordsDocumentSourcesWithOrdinalRank()
    {
        var store = new TestLibraryStore();
        store.Libraries.Add(TestLibrary());
        store.Matches.Add(new(TestDocument(), DocumentMatchType.Both, 0.9, 0.4));
        store.Matches.Add(
            new(
                TestDocument("doc_2", "/docs/other.md", "Other"),
                DocumentMatchType.FullText,
                0.5,
                0
            )
        );

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchDocuments(store, TestUser(), "kb", "retries");
        }

        await Assert.That(sink.Sources.Count).IsEqualTo(2);

        var first = sink.Sources[0];
        await Assert.That(first.ToolName).IsEqualTo("search_documents");
        await Assert.That(first.Kind).IsEqualTo(ToolKnowledgeSourceKind.Document);
        await Assert.That(first.Usage).IsEqualTo(ToolKnowledgeSourceUsage.Searched);
        await Assert.That(first.DocumentId).IsEqualTo("doc_1");
        await Assert.That(first.Rank).IsEqualTo(1);
        await Assert.That(first.Score).IsEqualTo(0);

        await Assert.That(sink.Sources[1].DocumentId).IsEqualTo("doc_2");
        await Assert.That(sink.Sources[1].Rank).IsEqualTo(2);
    }

    [Test]
    public async Task SearchDocuments_ZeroResults_RecordsMissedQuery()
    {
        var store = new TestLibraryStore();
        store.Libraries.Add(TestLibrary());

        var sink = new RecordingSink();
        using (ToolTelemetrySink.BeginScope(sink))
        {
            await DocumentLibraryMcpTools.SearchDocuments(store, TestUser(), "kb", "no hits");
        }

        await Assert.That(sink.Sources).IsEmpty();
        var miss = sink.Misses.Single();
        await Assert.That(miss.Tool).IsEqualTo("search_documents");
        await Assert.That(miss.Query).IsEqualTo("no hits");
    }

    private static ClaimsPrincipal TestUser() =>
        new(new ClaimsIdentity([new Claim(AuthClaims.OrganizationId, "org_123")], "test"));

    private static Library TestLibrary() =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            Name = "kb",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static LibraryDocument TestDocument(
        string id = "doc_1",
        string path = "/docs/guide.md",
        string title = "Guide"
    ) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            LibraryId = "lib_123",
            Path = path,
            Title = title,
            Content = "# Guide\n\nUse exponential backoff.",
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

    private static SnippetSearchRow CodeRow() =>
        new(
            SnippetId: "snip_2",
            DocumentId: "doc_1",
            DocumentPath: "/docs/guide.md",
            DocumentTitle: "Guide",
            Header: "Retry Policy",
            HeadingPath: "Guide > Retry Policy",
            Language: "csharp",
            Tag: "example",
            Content: "new ClientRetryPolicy(5);",
            TokenCount: 6,
            Score: 0.5,
            VectorRank: 1,
            TextRank: 1,
            IdentifierMatch: true
        );

    private static SnippetSearchService BuildSnippetService(
        out TestLibraryStore libraries,
        out FakeSnippetStore snippetStore
    )
    {
        libraries = new TestLibraryStore();
        snippetStore = new FakeSnippetStore();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        return new SnippetSearchService(
            libraries,
            snippetStore,
            new FakeSnippetStore(),
            new FakeEmbeddingGenerator(),
            new LlmEmbeddingSettings(),
            cache,
            NullLogger<SnippetSearchService>.Instance
        );
    }

    /// <summary>Captures the sources and misses recorded through the ambient sink during a call.</summary>
    private sealed class RecordingSink : IToolTelemetrySink
    {
        public List<ToolKnowledgeSource> Sources { get; } = [];
        public List<(string Tool, string Query)> Misses { get; } = [];

        public void RecordSource(ToolKnowledgeSource source) => Sources.Add(source);

        public void RecordMissedQuery(string toolName, string query) =>
            Misses.Add((toolName, query));
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

    /// <summary>
    /// Library store fake serving the three lookups the document tools use: library resolution,
    /// document-by-path read, and combined document search. All other members throw.
    /// </summary>
    /// <remarks>
    /// NOTE: this is intentionally a telemetry-only stub — it verifies that the read and
    /// search-documents tools EMIT the right sources on success/miss, not the store's tiered
    /// path-resolution semantics (those are covered by the store/handler tests). It keys documents
    /// by exact normalized path, which is sufficient because the tools normalize before calling.
    /// To catch contract drift, <c>GetByPathAsync</c>/<c>SearchAsync</c> assert the tool resolved
    /// the library first (org + library id must match a library in <see cref="Libraries" />) rather
    /// than blindly returning. Follows the established per-file fake precedent in this suite (see
    /// <c>DocumentLibraryMcpToolsSearchSnippetsTests</c>), extended with the read/search members.
    /// </remarks>
    private sealed class TestLibraryStore : ILibraryDocumentStore
    {
        public List<Library> Libraries { get; } = [];
        public Dictionary<string, LibraryDocument> Documents { get; } = [];
        public List<LibraryDocumentMatch> Matches { get; } = [];

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

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        )
        {
            EnsureLibraryResolved(organizationId, libraryId);

            return Task.FromResult(Documents.GetValueOrDefault(input));
        }

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        )
        {
            EnsureLibraryResolved(organizationId, libraryId);

            return Task.FromResult<IReadOnlyList<LibraryDocumentMatch>>(
                Matches.Take(limit).ToArray()
            );
        }

        /// <summary>
        /// Fails loudly if the tool reached the store without first resolving the library from
        /// claims — the org + library id must match a known library.
        /// </summary>
        private void EnsureLibraryResolved(string organizationId, string libraryId)
        {
            if (
                !Libraries.Any(library =>
                    library.OrganizationId == organizationId && library.Id == libraryId
                )
            )
            {
                throw new InvalidOperationException(
                    $"Store was called with an unresolved library (org='{organizationId}', "
                        + $"libraryId='{libraryId}'); the tool must resolve the library from claims first."
                );
            }
        }

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

        public Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(
            SnippetSearchQuery query,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<SnippetSearchRow>>(Rows.Take(query.Limit).ToArray());

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
