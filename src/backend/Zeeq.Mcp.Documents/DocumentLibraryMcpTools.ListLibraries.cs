using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using ModelContextProtocol.Server;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Platform.Documents;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> ListLibrariesCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_list_libraries_total",
            "The total number of times the list libraries MCP tool is called."
        );

    /// <summary>
    /// Lists the manual document libraries in the caller's active organization.
    /// </summary>
    /// <remarks>
    /// This is the discovery entry point for the document-library tools. Every document tool
    /// (<c>list_documents</c>, <c>read_document_by_path</c>, <c>search_documents</c>) needs a library
    /// name, so agents call this first to learn which libraries are available. The organization is
    /// taken from the server-issued claims on the authenticated principal, never from caller input,
    /// so an agent cannot read another tenant's libraries.
    /// </remarks>
    /// <param name="store">The injected document-library store.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A JSON array of libraries in the active organization.</returns>
    [McpServerTool(Name = "list_libraries", Title = "List Libraries")]
    [Description(
        """
            Lists the document libraries that are available in the organization.
            Each library contains important knowledge and guidance for working in this codebase
            """
    )]
    public static async Task<string> ListLibraries(
        ILibraryDocumentStore store,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default
    )
    {
        var organizationId = user?.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return RecordToolCall(
                ListLibrariesCounter,
                user,
                "no_active_org",
                [],
                "Active organization is required."
            );
        }

        var libraries = await store.ListLibrariesAsync(organizationId, cancellationToken);

        var buffer = new StringBuilder();

        buffer.Append(
            """

            (library_name: library_description)
            ---

            """
        );

        return RecordToolCall(
            ListLibrariesCounter,
            user,
            "success",
            [("organization", organizationId)],
            libraries
                .Aggregate(
                    buffer,
                    (acc, lib) =>
                        acc.AppendLine(
                            $"""
                            {lib.Name}: "{lib.RenderedDescription}"
                            """
                        )
                )
                .ToString()
        );
    }
}
