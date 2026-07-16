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

public sealed class DocumentLibraryMcpToolsSearchSnippetsTests
{
    [Test]
    public async Task SearchSections_WithoutOrganizationClaim_ReturnsValidationError()
    {
        var service = BuildService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            anonymous,
            "kb",
            "retries"
        );

        await Assert.That(response).IsEqualTo("Active organization is required.");
        await Assert.That(snippetStore.LastQuery).IsNull();
    }

    [Test]
    public async Task SearchSections_WithUnknownLibrary_ReturnsNotFoundError()
    {
        var service = BuildService(out _, out _);

        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "does-not-exist",
            "retries"
        );

        await Assert
            .That(response)
            .IsEqualTo(
                "Library 'does-not-exist' was not found; use the list_libraries tool to get valid libraries."
            );
    }

    [Test]
    public async Task SearchSections_HonorsExcludedDocumentPaths()
    {
        var service = BuildService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "kb",
            "retries",
            excludeDocumentPaths: ["/docs/other.md"]
        );

        await Assert
            .That(snippetStore.LastQuery!.ExcludedDocumentPaths)
            .IsEquivalentTo(["/docs/other.md"]);
    }

    [Test]
    public async Task SearchSections_WithNoMatches_SuggestsBroadening()
    {
        var service = BuildService(out var libraries, out _);
        libraries.Libraries.Add(TestLibrary());

        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "kb",
            "retries"
        );

        await Assert.That(response).Contains("No results for");
        await Assert.That(response).Contains("broader terms");
    }

    [Test]
    public async Task SearchSections_WithOneResult_FormatsMarkdownGroupedByDocument()
    {
        var service = BuildService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "kb",
            "retries"
        );

        await Assert.That(response).Contains("# Results for: \"retries\"");
        await Assert.That(response).Contains("## Guide — `zeeq://docs/guide.md` (library: kb)");
        await Assert.That(response).Contains("### Guide > Retries");
        await Assert.That(response).Contains("Use exponential backoff.");
        await Assert.That(response).DoesNotContain("Semantic ranking was unavailable");
    }

    [Test]
    public async Task SearchCodeSnippets_WithOneResult_WrapsContentInLanguageFence()
    {
        var service = BuildService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(CodeRow());

        var response = await DocumentLibraryMcpTools.SearchCodeSnippets(
            service,
            TestUser(),
            "kb",
            "retry policy"
        );

        await Assert.That(response).Contains("```csharp");
        await Assert.That(response).Contains("new ClientRetryPolicy(5);");
        await Assert.That(response).Contains("## Guide — `zeeq://docs/guide.md` (library: kb)");
    }

    /// <summary>
    /// Closes the code-fence-collision gap code review flagged (2026-07-11): a snippet body
    /// containing a triple-backtick run must not be able to prematurely close the wrapping fence.
    /// </summary>
    [Test]
    public async Task SearchCodeSnippets_WithEmbeddedBackticks_WidensFenceToAvoidCollision()
    {
        var service = BuildService(out var libraries, out var snippetStore);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(CodeRow() with { Content = "Example:\n```\nsome nested fence\n```" });

        var response = await DocumentLibraryMcpTools.SearchCodeSnippets(
            service,
            TestUser(),
            "kb",
            "retry policy"
        );

        await Assert.That(response).Contains("````csharp");
        await Assert.That(response).Contains("some nested fence");
    }

    [Test]
    public async Task SearchSections_WhenEmbeddingFails_AppendsDegradedModeNote()
    {
        var service = BuildService(out var libraries, out var snippetStore, throwOnEmbed: true);
        libraries.Libraries.Add(TestLibrary());
        snippetStore.Rows.Add(SectionRow());

        var response = await DocumentLibraryMcpTools.SearchSections(
            service,
            TestUser(),
            "kb",
            "retries"
        );

        await Assert.That(response).Contains("Semantic ranking was unavailable");
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

    private static SnippetSearchService BuildService(
        out FakeLibraryDocumentStore libraries,
        out FakeSnippetStore snippetStore,
        bool throwOnEmbed = false
    )
    {
        libraries = new FakeLibraryDocumentStore();
        snippetStore = new FakeSnippetStore();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        IEmbeddingGenerator<string, Embedding<float>> generator = throwOnEmbed
            ? new ThrowingEmbeddingGenerator()
            : new FakeEmbeddingGenerator();

        return new SnippetSearchService(
            libraries,
            snippetStore,
            new FakeSnippetStore(),
            generator,
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

    // NOTE: near-duplicate of the fake in DocumentLibraryMcpToolsTests.cs (TestLibraryDocumentStore)
    // and SnippetSearchServiceTests.cs (FakeLibraryDocumentStore) — same established precedent as
    // the product code's public/private snippet store duplication; this test only needs
    // GetLibraryAsync.
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

    /// <remarks>
    /// NOTE: implementing both closed generics on one class was flagged by code review
    /// (2026-07-11) as a compile-time contract risk if the two closures ever diverge beyond
    /// <c>ReplaceForDocumentAsync</c>. Empirically verified, not just asserted — see the matching
    /// NOTE on <c>SnippetSearchServiceTests.FakeSnippetStore</c> for the equivalent
    /// dispatch-scoping assertions in that test suite.
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
