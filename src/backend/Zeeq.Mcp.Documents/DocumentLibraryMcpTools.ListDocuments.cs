using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Hybrid;
using ModelContextProtocol.Server;
using ToonSharp;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> ListDocumentsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_list_documents_total",
            "The total number of times the list documents MCP tool is called."
        );

    private static readonly ToonSerializerOptions ListDocumentsToonOptions = new();

    private static readonly HybridCacheEntryOptions ListDocumentsCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(120),
    };

    /// <summary>
    /// Lists the documents in a named manual document library.
    /// </summary>
    /// <remarks>
    /// This is the table-of-contents path for a single library. It returns a compact index (no
    /// content) so an agent can map a library before reading or searching it. The index is a
    /// path tree rendered as TOON rather than a flat JSON array: directory segments become
    /// nesting, so a document's path is never repeated per-document. When every document shares
    /// a root directory chain, it is reported once as a <c>Path root:</c> preamble line instead
    /// of a no-op top-level key. See <see cref="BuildDocumentPathTree"/> for the folding rules.
    /// <para/>
    /// The successful result depends only on the caller's organization and the resolved library,
    /// so it is cached in <see cref="HybridCache"/> for <see cref="ListDocumentsCacheOptions"/>'s
    /// TTL, keyed by organization id and the resolved library's stable id (not the caller's raw
    /// library name string) — the id is already in hand from <see cref="ResolveLibraryAsync"/>, so
    /// keying on it costs nothing extra and, unlike the name, can never collide: library names are
    /// case-sensitive and unique-per-org only up to exact string equality (a "KB" and a "kb" could
    /// coexist as two different libraries), so a normalized (lowercased) name could otherwise map
    /// two distinct libraries onto the same cache entry. Resolution errors (missing org, unknown
    /// library) are not cached — they're cheap to recompute and caching a stale "not found" could
    /// hide a library the caller just created.
    /// </remarks>
    /// <param name="store">The injected document-library store.</param>
    /// <param name="cache">The injected hybrid cache backing the 120-second response cache.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name to list documents from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// A TOON-encoded path tree of the library's documents, keyed by folded path segment,
    /// preceded by a <c>Path root:</c> line when a common root directory chain was stripped.
    /// </returns>
    [McpServerTool(Name = "list_documents", Title = "List Documents")]
    [Description(
        """
            Use once near the start of an agent session, or before first knowledge-base library
            research, to discover what canonical documents are available.

            Returns a dense TOON path-tree index of KB documents (not JSON, not flat); the
            response itself explains how to reconstruct each document's full path.

            <list_kb_documents.triggers>
            - Start of session, first KB lookup, project onboarding, or repo exploration
            - Need to know what docs, guidance, best practices, references, or research sources exist
            - Before choosing which KB document to read or which semantic KB search to run
            - Explicit trigger words like: check documents, check library, refer to docs, read KB, and similar
            </list_kb_documents.triggers>

            Call this only once per session and reuse the result unless the document
            library or target partition may have changed.
            """
    )]
    public static async Task<string> ListDocuments(
        ILibraryDocumentStore store,
        HybridCache cache,
        ClaimsPrincipal? user,
        [Description("Required library name; use `list_libraries` to see available libraries.")]
            string library,
        CancellationToken cancellationToken = default
    )
    {
        var resolved = await ResolveLibraryAsync(store, user, library, cancellationToken);

        if (resolved.Error is not null)
        {
            return RecordToolCall(
                ListDocumentsCounter,
                user,
                "library_error",
                [("library", library)],
                resolved.Error
            );
        }

        // ResolveLibraryAsync only returns a null Error alongside a non-null Library, so this
        // can't currently happen — but the cache key and store call below need a concrete id, so
        // this stays an explicit checked branch (a caller-facing message) rather than a `!`
        // null-forgiving assertion that would throw if that contract ever drifted.
        if (resolved.Library is null)
        {
            return RecordToolCall(
                ListDocumentsCounter,
                user,
                "library_error",
                [("library", library)],
                $"Library '{library}' could not be resolved; use the list_libraries tool to get valid libraries."
            );
        }

        RecordKnowledgeRead(
            DocumentReadCounter,
            user,
            resolved.OrganizationId,
            "list_documents",
            library,
            null
        );

        var cacheKey = $"mcp:list_documents:{resolved.OrganizationId}:{resolved.Library.Id}";

        var body = await cache.GetOrCreateAsync(
            cacheKey,
            (Store: store, OrganizationId: resolved.OrganizationId, LibraryId: resolved.Library.Id),
            static async (state, ct) =>
            {
                var documents = await state.Store.ListDocumentsAsync(
                    state.OrganizationId,
                    state.LibraryId,
                    ct
                );

                var (tree, rootPrefix) = BuildDocumentPathTree(documents);
                // TOON over the folded tree runs ~60-90% smaller than flat JSON, growing with
                // library size/uniformity (measured against zeeq-app and zeeq-docs).
                var toon = ToonSerializer.Serialize(tree, ListDocumentsToonOptions);
                return rootPrefix is null
                    ? $"""
                        Join key's full branch (segments joined by "/") to get document's path.

                        {toon}
                        """
                    : $"""
                        Path root: zeeq://{rootPrefix}

                        Join path root with key's full branch (segments joined by "/") to get document's path.

                        {toon}
                        """;
            },
            ListDocumentsCacheOptions,
            cancellationToken: cancellationToken
        );

        return RecordToolCall(
            ListDocumentsCounter,
            user,
            "success",
            [("organization", resolved.OrganizationId), ("library", library)],
            body
        );
    }

    /// <summary>
    /// Builds the folded path tree rendered by <see cref="ListDocuments"/>: directory segments
    /// become nested TOON objects and each document becomes a leaf array of its keywords.
    /// </summary>
    /// <remarks>
    /// Two foldings keep the tree from repeating text the caller can already infer:
    /// <list type="bullet">
    /// <item>A root chain shared by every document in the library (for example a fixed
    /// <c>web/content/docs</c> prefix) carries no branching information as a nesting level, so
    /// it is pulled out into the returned <c>RootPrefix</c> instead of being emitted as a no-op
    /// top-level key — the caller still needs it to reconstruct a full path, just once instead
    /// of once per document.</item>
    /// <item>Below the root, any directory whose only child is itself a directory (not yet a
    /// document) is merged into its child's key as <c>parent/child</c>, so a chain of
    /// single-child folders collapses to one line instead of one nesting level each.</item>
    /// </list>
    /// A leaf renders as a bare keyword array (<c>"file.md"[N]: ...</c>) rather than an object
    /// with a <c>title</c> field: the filename in the path is already the title's information;
    /// repeating it per document would undo the point of folding by path.
    /// </remarks>
    private static (JsonObject Tree, string? RootPrefix) BuildDocumentPathTree(
        IReadOnlyList<LibraryDocument> documents
    )
    {
        var root = new DocumentPathNode();
        foreach (var document in documents)
        {
            var segments = document.Path.Trim('/').Split('/');
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                if (!current.Children.TryGetValue(segments[i], out var next))
                {
                    next = new DocumentPathNode();
                    current.Children[segments[i]] = next;
                }
                current = next;

                if (i == segments.Length - 1)
                {
                    current.Keywords = document.Keywords;
                }
            }
        }

        // Peel off a root chain every document shares — it distinguishes nothing as a nesting
        // level. Stop one directory short of a leaf: descending into the leaf itself would leave
        // nothing for DocumentPathNodeToJson to render (it renders a node's CHILDREN, and a leaf
        // has none), silently dropping the one document a single-document library (or a lone
        // document at the end of an otherwise-empty directory chain) contains.
        // NOTE: a document directly under one directory (e.g. "guides/intro.md") is not lost
        // here — the check below breaks before descending into that document, so effectiveRoot
        // stops at "guides" and the leaf renders normally under it on the next call.
        var effectiveRoot = root;
        List<string>? strippedSegments = null;
        while (effectiveRoot.Children.Count == 1)
        {
            var (onlySegment, onlyChild) = effectiveRoot.Children.First();
            if (onlyChild.Keywords is not null)
            {
                break;
            }

            (strippedSegments ??= []).Add(onlySegment);
            effectiveRoot = onlyChild;
        }

        return (
            DocumentPathNodeToJson(effectiveRoot),
            strippedSegments is null ? null : string.Join('/', strippedSegments)
        );
    }

    /// <summary>
    /// Recursively renders a <see cref="DocumentPathNode"/>'s children, folding single-child
    /// directory chains into one combined key before descending.
    /// </summary>
    private static JsonObject DocumentPathNodeToJson(DocumentPathNode node)
    {
        var result = new JsonObject();
        foreach (var (segment, child) in node.Children)
        {
            var key = segment;
            var current = child;
            while (current.Keywords is null && current.Children.Count == 1)
            {
                var (childSegment, grandchild) = current.Children.First();
                key = $"{key}/{childSegment}";
                current = grandchild;
            }

            result[key] = current.Keywords is not null
                ? new JsonArray(current.Keywords.Select(k => (JsonNode)k).ToArray())
                : DocumentPathNodeToJson(current);
        }
        return result;
    }

    /// <summary>
    /// A directory or document in the path tree built by <see cref="BuildDocumentPathTree"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Keywords"/> being non-null marks this node as a document (a leaf); a node with
    /// both children and keywords cannot occur because <see cref="LibraryDocument.Path"/> is
    /// unique per library, so no path is ever a strict prefix of another path in the same store.
    /// </remarks>
    private sealed class DocumentPathNode
    {
        public SortedDictionary<string, DocumentPathNode> Children { get; } =
            new(StringComparer.Ordinal);

        public string[]? Keywords { get; set; }
    }
}
