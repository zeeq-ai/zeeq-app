using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> ListDocumentsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_list_documents_total",
            "The total number of times the list documents MCP tool is called."
        );

    /// <summary>
    /// Lists the documents in a named manual document library.
    /// </summary>
    /// <remarks>
    /// This is the table-of-contents path for a single library. It returns compact summaries (no
    /// content) ordered by path so an agent can map a library before reading or searching it.
    /// </remarks>
    /// <param name="store">The injected document-library store.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name to list documents from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A JSON array of document summaries ordered by path.</returns>
    [McpServerTool(Name = "list_documents", Title = "List Documents")]
    [Description(
        """
            Use once near the start of an agent session, or before first knowledge-base library
            research, to discover what canonical documents are available.

            Returns a compact table-of-contents / dense index of KB documents by partition,
            including paths and keywords. This helps map the available expert guidance before
            planning, researching, implementing, debugging, or reviewing code.

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

        var documents = await store.ListDocumentsAsync(
            resolved.OrganizationId,
            resolved.Library!.Id,
            cancellationToken
        );

        RecordKnowledgeRead(
            DocumentReadCounter,
            user,
            resolved.OrganizationId,
            "list_documents",
            library,
            null
        );

        return RecordToolCall(
            ListDocumentsCounter,
            user,
            "success",
            [("organization", resolved.OrganizationId), ("library", library)],
            ToJson(documents.Select(ToSummary))
        );
    }
}
