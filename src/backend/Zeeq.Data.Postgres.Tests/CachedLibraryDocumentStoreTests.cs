using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Unit tests for cached document path resolution.
///
/// Run this class:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/CachedLibraryDocumentStoreTests/*"
/// </summary>
public sealed class CachedLibraryDocumentStoreTests
{
    [Test]
    public async Task GetByPath_CachesResult_SecondCallHitsCache()
    {
        var inner = new CountingLibraryDocumentStore(
            new LibraryDocument
            {
                Id = "document_1",
                OrganizationId = "org_1",
                LibraryId = "library_1",
                Path = "/guides/start.md",
                Title = "Start",
                TitleNormalized = "start",
                Content = "Start here.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        var store = new CachedLibraryDocumentStore(inner, new TestHybridCache());

        var first = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "Guides/Start",
            CancellationToken.None
        );
        var second = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "Guides/Start",
            CancellationToken.None
        );

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(inner.PathLookupCount).IsEqualTo(1);
        await Assert.That(inner.PathLookupInputs).IsEquivalentTo(["/guides/start.md"]);
    }

    [Test]
    public async Task GetByPath_CacheKey_IncludesLibrary()
    {
        var inner = new CountingLibraryDocumentStore(CreateDocument("library_1"));
        var cache = new TestHybridCache();
        var store = new CachedLibraryDocumentStore(inner, cache);

        await store.GetByPathAsync("org_1", "library_1", "doc", CancellationToken.None);
        await store.GetByPathAsync("org_1", "library_2", "doc", CancellationToken.None);

        await Assert.That(inner.PathLookupCount).IsEqualTo(2);
        await Assert
            .That(cache.Keys)
            .IsEquivalentTo([
                "doc:path:org_1:library_1:/doc.md",
                "doc:path:org_1:library_2:/doc.md",
            ]);
    }

    [Test]
    public async Task GetByPath_CacheOptions_UseSixtySecondL2OnlyTtl()
    {
        var inner = new CountingLibraryDocumentStore(CreateDocument("library_1"));
        var cache = new TestHybridCache();
        var store = new CachedLibraryDocumentStore(inner, cache);

        await store.GetByPathAsync("org_1", "library_1", "doc", CancellationToken.None);

        await Assert.That(cache.LastOptions?.Expiration).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert
            .That(cache.LastOptions?.Flags)
            .IsEqualTo(HybridCacheEntryFlags.DisableLocalCache);
    }

    [Test]
    public async Task GetByPath_NotFound_DoesNotCacheMiss()
    {
        var inner = new CountingLibraryDocumentStore(null);
        var cache = new TestHybridCache();
        var store = new CachedLibraryDocumentStore(inner, cache);

        var first = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "missing",
            CancellationToken.None
        );
        var second = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "missing",
            CancellationToken.None
        );

        await Assert.That(first).IsNull();
        await Assert.That(second).IsNull();
        await Assert.That(inner.PathLookupCount).IsEqualTo(2);
        await Assert.That(cache.RemoveCount).IsEqualTo(2);
    }

    [Test]
    public async Task UpsertDocument_InvalidatesCachedPathLookupsForLibrary()
    {
        var inner = new CountingLibraryDocumentStore(CreateDocument("library_1", "Old content."));
        var cache = new TestHybridCache();
        var store = new CachedLibraryDocumentStore(inner, cache);

        var cached = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "doc",
            CancellationToken.None
        );
        await store.UpsertDocumentAsync(
            CreateDocument("library_1", "New content."),
            CancellationToken.None
        );
        var refreshed = await store.GetByPathAsync(
            "org_1",
            "library_1",
            "doc",
            CancellationToken.None
        );

        await Assert.That(cached?.Content).IsEqualTo("Old content.");
        await Assert.That(refreshed?.Content).IsEqualTo("New content.");
        await Assert.That(inner.PathLookupCount).IsEqualTo(2);
        await Assert.That(cache.RemovedTags).IsEquivalentTo(["doc:path:library:org_1:library_1"]);
    }

    private static LibraryDocument CreateDocument(
        string libraryId,
        string content = "Doc content."
    ) =>
        new()
        {
            Id = $"document_{libraryId}",
            OrganizationId = "org_1",
            LibraryId = libraryId,
            Path = "/doc.md",
            Title = "Doc",
            TitleNormalized = "doc",
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class CountingLibraryDocumentStore(LibraryDocument? document)
        : ILibraryDocumentStore
    {
        private LibraryDocument? _document = document;

        public int PathLookupCount { get; private set; }
        public List<string> PathLookupInputs { get; } = [];

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
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
        )
        {
            _document = document;

            return Task.FromResult(document);
        }

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        )
        {
            PathLookupCount++;
            PathLookupInputs.Add(input);

            return Task.FromResult(_document?.LibraryId == libraryId ? _document : null);
        }

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
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

    private sealed class TestHybridCache : HybridCache
    {
        private readonly Dictionary<string, object?> _values = [];
        private readonly Dictionary<string, HashSet<string>> _tagsByKey = [];

        public List<string> Keys { get; } = [];
        public List<string> RemovedTags { get; } = [];
        public HybridCacheEntryOptions? LastOptions { get; private set; }
        public int RemoveCount { get; private set; }

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            Keys.Add(key);
            LastOptions = options;
            _tagsByKey[key] = tags?.ToHashSet(StringComparer.Ordinal) ?? [];

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
            RemoveCount++;
            _values.Remove(key);

            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(
            string tag,
            CancellationToken cancellationToken = default
        )
        {
            RemovedTags.Add(tag);

            foreach (
                var key in _tagsByKey
                    .Where(pair => pair.Value.Contains(tag))
                    .Select(pair => pair.Key)
                    .ToArray()
            )
            {
                _values.Remove(key);
                _tagsByKey.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }
}
