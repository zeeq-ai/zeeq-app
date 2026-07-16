using Zeeq.Core.Documents;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.CodeReviews;

internal static class ILibraryDocumentStoreExtensions
{
    private static readonly HybridCacheEntryOptions LibraryListCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(30),
        LocalCacheExpiration = TimeSpan.FromMinutes(10),
    };

    extension(ILibraryDocumentStore store)
    {
        /// <summary>
        /// Lists the names of libraries whose IDs appear in <paramref name="libraryIds"/>,
        /// using <paramref name="cache"/> to avoid a round-trip on every review when the
        /// org's library list has not changed.
        /// Returns an empty array when <paramref name="libraryIds"/> is null or empty.
        /// </summary>
        public async Task<string[]> ResolveMappedLibraryNamesAsync(
            string organizationId,
            string[]? libraryIds,
            HybridCache cache,
            CancellationToken ct
        )
        {
            if (libraryIds is not { Length: > 0 })
                return [];

            // T must be a concrete array — HybridCache cannot deserialize interface types from L2.
            var all = await cache.GetOrCreateAsync<Library[]>(
                $"libs:list:{organizationId}",
                async cancellationToken =>
                    [.. await store.ListLibrariesAsync(organizationId, cancellationToken)],
                LibraryListCacheOptions,
                cancellationToken: ct
            );

            return [.. all.Where(l => libraryIds.Contains(l.Id)).Select(l => l.Name)];
        }
    }
}
