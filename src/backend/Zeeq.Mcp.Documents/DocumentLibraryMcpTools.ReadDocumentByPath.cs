using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> ReadDocumentByPathCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_read_document_by_path_total",
            "The total number of times the read document by path MCP tool is called."
        );

    /// <summary>
    /// Reads the full markdown content of a document in a manual document library.
    /// </summary>
    /// <remarks>
    /// Resolution is tiered: the exact normalized path first, then progressively shorter path
    /// suffixes, then the bare file name. A partial path therefore still resolves to the right
    /// document, and the most specific candidate wins.
    /// </remarks>
    /// <param name="store">The injected document-library store.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name that contains the document.</param>
    /// <param name="path">The document path, suffix path, or file name to resolve.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The markdown content of the document, or a not-found message.</returns>
    [McpServerTool(Name = "read_document_by_path", Title = "Read Document By Path")]
    [Description(
        """
            Use to read the full markdown content of a specific document in a document library.

            Use `list_documents` to get the list of documents available in the library.

            <read_document_by_path.triggers>
            - You identified a document path from `list_documents` or `search_documents` and need its full content
            - zeeq:// prefixed paths ending in `.md` are explicit requests to the Zeeq MCP to read a document in the library
            - A `search_documents` result points to a document you have not read yet
            - You need the complete document rather than the ranked snippets a search returns
            - Planning, implementing, or reviewing work that requires comprehensive guidance from a specific document
            </read_document_by_path.triggers>

            Use `list_documents` to discover available paths.
            Do NOT re-read a document you already retrieved in this session.
            """
    )]
    public static async Task<string> ReadDocumentByPath(
        ILibraryDocumentStore store,
        ClaimsPrincipal? user,
        [Description("Required library name; use `list_libraries` to see available libraries.")]
            string library,
        [Description(
            "Document path, suffix path, or file name to resolve; use `list_documents` to get available paths."
        )]
            string path,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return RecordToolCall(
                ReadDocumentByPathCounter,
                user,
                "missing_path",
                [("library", library)],
                "path is required."
            );
        }

        // This double normalizes because the cached store also normalizes, but this
        // is safer in case we change implementations in the future.
        var normalizedPath = DocumentNormalizer.NormalizePath(path);

        var resolved = await ResolveLibraryAsync(store, user, library, cancellationToken);

        if (resolved.Error is not null)
        {
            return RecordToolCall(
                ReadDocumentByPathCounter,
                user,
                "library_error",
                [("library", library)],
                resolved.Error
            );
        }

        var document = await store.GetByPathAsync(
            resolved.OrganizationId,
            resolved.Library!.Id,
            normalizedPath,
            cancellationToken
        );

        (string Key, object? Value)[] scope =
        [
            ("organization", resolved.OrganizationId),
            ("library", library),
        ];

        if (document is null)
        {
            return RecordToolCall(
                ReadDocumentByPathCounter,
                user,
                "not_found",
                scope,
                $"Document '{path}' was not found in library '{library}'."
            );
        }

        // Best-effort source attribution (no-op outside a code-review run). A direct read is a
        // Document-kind Read hit with no query; the collector uses the Searched→Read funnel
        // (readAfterSearch) as a strong relevance proxy when the same document was also searched.
        ToolTelemetrySink.RecordSource(
            new(
                ToolName: "read_document_by_path",
                Kind: ToolKnowledgeSourceKind.Document,
                Usage: ToolKnowledgeSourceUsage.Read,
                Library: resolved.Library!.Name,
                DocumentPath: document.Path,
                DocumentTitle: document.Title,
                DocumentId: document.Id
            )
        );

        RecordKnowledgeRead(
            DocumentReadCounter,
            user,
            resolved.OrganizationId,
            "read_document_by_path",
            library,
            document.Path
        );

        return RecordToolCall(ReadDocumentByPathCounter, user, "success", scope, document.Content);
    }
}
