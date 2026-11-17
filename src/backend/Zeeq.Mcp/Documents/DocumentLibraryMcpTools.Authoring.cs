using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zeeq.Core.Auditing;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Documents.Editing;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> UpdateDocumentCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_update_document_by_diff_total");
    private static readonly Counter<int> CreateDocumentCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_create_document_total");

    /// <summary>Applies an approved unified diff to a locally owned document.</summary>
    [McpServerTool(Name = "update_document_by_diff", Title = "Update Document By Diff")]
    [Description(
        """
            Change a Zeeq-owned canonical document with a unified diff against the version you read.
            <update_document_by_diff.triggers>
            Use when a user asks to correct or extend canonical guidance. Do not edit synced documents; use their origin.
            </update_document_by_diff.triggers>
            <update_document_by_diff.flow>
            Read the document and retain contentHash. Edit a copy and generate `diff -U3`. Show the user the diff and get approval. Call this tool with that hash and a rationale. On stale, rebuild the edit against the returned current content and show the revised diff before retrying. Never retry a rejected diff unchanged.
            </update_document_by_diff.flow>
            STYLE: Match the document's tone, voice, heading depth, terminology, formatting, and code examples. Make the smallest change needed.
            One file per diff; use at least three unchanged context lines except at file boundaries. Do not use ellipses. Hunks must be in document order. Result must not be empty and must be at most 200000 characters.
            """
    )]
    public static async Task<CallToolResult> UpdateDocumentByDiff(
        ILibraryDocumentStore store,
        DocumentAuthoringService authoring,
        IAuditActorAccessor actors,
        IHttpContextAccessor http,
        ClaimsPrincipal? user,
        [Description("Required library name.")] string library,
        [Description("Required document path from read_document_by_path.")] string path,
        [Description("Required contentHash from the read, never computed by the agent.")]
            string baseHash,
        [Description("Required unified diff for one document.")] string diff,
        [Description("Required one or two sentence rationale stored in audit.")] string rationale,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(baseHash)
            || string.IsNullOrWhiteSpace(diff)
            || string.IsNullOrWhiteSpace(rationale)
            || rationale.Length > DocumentAuthoringService.MaxRationaleLength
        )
            return Counted(
                UpdateDocumentCounter,
                "invalid_input",
                "status: rejected\ncode: invalid_input\nsaved: false\npath, baseHash, diff and rationale are required; rationale must be at most 2000 characters."
            );
        var resolved = await ResolveLibraryAsync(store, user, library, cancellationToken);
        if (resolved.Error is not null)
            return Counted(UpdateDocumentCounter, "library_error", resolved.Error);
        try
        {
            var result = await authoring.UpdateAsync(
                resolved.OrganizationId,
                resolved.Library!,
                path,
                baseHash,
                diff,
                rationale,
                actors.Current,
                McpRequestTelemetryContext.From(http.HttpContext)?.ClientName,
                cancellationToken
            );
            var message = RenderUpdate(library, result, rationale);
            UpdateDocumentCounter.Add(
                1,
                new KeyValuePair<string, object?>("outcome", result.Status.ToCode())
            );
            if (result.Outcome is DiffOutcome.Stale stale)
            {
                var current = JsonSerializer.Serialize(
                    new { contentHash = stale.CurrentHash, content = stale.CurrentContent }
                );
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock { Text = message },
                        new TextContentBlock { Text = current },
                    ],
                };
            }
            return TextResult(message);
        }
        catch (OperationCanceledException)
        {
            UpdateDocumentCounter.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
            throw;
        }
        catch (Exception)
        {
            return Counted(
                UpdateDocumentCounter,
                "error",
                "status: error\nsaved: unknown\nDocument edit could not be confirmed; read the path before retrying."
            );
        }
    }

    /// <summary>Creates a new agent-authored document without overwriting.</summary>
    [McpServerTool(Name = "create_document", Title = "Create Document")]
    [Description(
        """
            Create a new markdown document in a library. It is live on save, marked agent-authored, and never overwrites a document.
            <create_document.triggers>
            Use when a user requests new canonical guidance that existing documents do not cover. Use update_document_by_diff for an existing document.
            </create_document.triggers>
            <create_document.flow>
            List documents, confirm the gap, and read sibling documents. Draft in their style and show the user for approval. Supply the path, full markdown and rationale. If the path exists, read it or choose a more specific path.
            </create_document.flow>
            STYLE: Match sibling tone, voice, front matter, structure, heading depth and examples. Avoid boilerplate.
            """
    )]
    public static async Task<CallToolResult> CreateDocument(
        ILibraryDocumentStore store,
        DocumentAuthoringService authoring,
        IAuditActorAccessor actors,
        IHttpContextAccessor http,
        ClaimsPrincipal? user,
        [Description("Required library name.")] string library,
        [Description("Required descriptive markdown path; no . or .. segments.")] string path,
        [Description("Required complete markdown content.")] string content,
        [Description("Required one or two sentence rationale stored in audit.")] string rationale,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(content)
            || string.IsNullOrWhiteSpace(rationale)
            || rationale.Length > DocumentAuthoringService.MaxRationaleLength
        )
            return Counted(
                CreateDocumentCounter,
                "invalid_input",
                "status: rejected\ncode: invalid_input\nsaved: false\npath, content and rationale are required; rationale must be at most 2000 characters."
            );
        var resolved = await ResolveLibraryAsync(store, user, library, cancellationToken);
        if (resolved.Error is not null)
            return Counted(CreateDocumentCounter, "library_error", resolved.Error);
        try
        {
            var result = await authoring.CreateAsync(
                resolved.OrganizationId,
                resolved.Library!,
                path,
                content,
                rationale,
                actors.Current,
                McpRequestTelemetryContext.From(http.HttpContext)?.ClientName,
                cancellationToken
            );
            return Counted(
                CreateDocumentCounter,
                result.Status.ToCode(),
                RenderCreate(library, result)
                    + (
                        result.ListMayBeStale
                            ? "\nwarning: the document list may be temporarily stale."
                            : string.Empty
                    )
            );
        }
        catch (OperationCanceledException)
        {
            CreateDocumentCounter.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
            throw;
        }
        catch (Exception)
        {
            return Counted(
                CreateDocumentCounter,
                "error",
                "status: error\nsaved: unknown\nDocument creation could not be confirmed; read the path before retrying."
            );
        }
    }

    private static CallToolResult TextResult(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Counted(Counter<int> counter, string outcome, string text)
    {
        counter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return TextResult(text);
    }

    internal static string RenderUpdate(string library, AuthoringResult result, string rationale)
    {
        if (result.Status == AuthoringStatus.Applied)
            return $"status: applied\nDocument updated: {library}{result.Document!.Path}\npreviousHash: {result.PreviousHash}\nnewHash: {result.Document.ContentHash}\nrationale: {JsonSerializer.Serialize(rationale)}\n<instruction_for_agents>Tell the user the document was updated and that its audit snapshot supports manual recovery. Use newHash for the next edit.</instruction_for_agents>";
        if (result.Status == AuthoringStatus.Stale)
            return "status: stale\nsaved: false\n<instruction_for_agents>Rebuild the diff using the current content in the next JSON content block. Show the revised diff before retrying.</instruction_for_agents>";
        if (result.Status == AuthoringStatus.NotFound)
            return "status: rejected\ncode: not_found\nsaved: false";
        if (result.Status == AuthoringStatus.NotEditable && result.Document is not null)
        {
            var info = DocumentAuthoringMetadata.Describe(result.Document);
            var note =
                info.Source == "GitHub"
                && !DocumentAuthoringMetadata.HasOriginalGitHubPath(result.Document)
                    ? "\noriginNote: The stored path is normalized; verify filename casing if the link does not resolve."
                    : string.Empty;
            var editLink = info.OriginEdit is null
                ? string.Empty
                : $"\noriginEdit: {info.OriginEdit}";
            return $"status: rejected\ncode: not_editable\nsaved: false\nsource: {info.Source}\norigin: {info.Origin}{editLink}{note}\n<instruction_for_agents>Edit at the origin, or provide the origin and proposed change to the user. Do not copy the content into a new Zeeq document.</instruction_for_agents>";
        }
        var detail = (result.Outcome as DiffOutcome.Rejected)?.Detail
            ?? "Edit could not be applied.";
        var guidance = result.Status switch
        {
            AuthoringStatus.InvalidDiff => "Regenerate a valid unified diff for one document.",
            AuthoringStatus.ThinContext => "Regenerate the diff with enough surrounding context.",
            AuthoringStatus.NoMatch =>
                "Compare the indicated hunk with the current document and regenerate the diff.",
            AuthoringStatus.Ambiguous => "Add unique surrounding context to the indicated hunk.",
            AuthoringStatus.OutOfOrder => "Order the hunks by their position in the document.",
            AuthoringStatus.EmptyResult =>
                "Revise the edit so the resulting document is not empty.",
            AuthoringStatus.TooLarge =>
                "Reduce the edit or resulting document to meet the stated limit.",
            AuthoringStatus.ApplyFailed =>
                "Review the diff and regenerate it against the current document.",
            _ => "Review the edit and the rejection before retrying.",
        };
        return $"status: rejected\ncode: {result.Status.ToCode()}\nsaved: false\n{detail}\n<instruction_for_agents>{guidance} Do not resend the same rejected diff.</instruction_for_agents>";
    }

    private static string RenderCreate(string library, AuthoringResult result)
    {
        if (result.Status == AuthoringStatus.Created)
            return $"status: created\nDocument created: {library}{result.Document!.Path} (agent-authored, live now)\ncontentHash: {result.Document.ContentHash}\n<instruction_for_agents>Tell the user the path and use this hash for a later edit.</instruction_for_agents>";
        if (result.Status == AuthoringStatus.Exists && result.Document is not null)
        {
            var info = DocumentAuthoringMetadata.Describe(result.Document);
            var details = JsonSerializer.Serialize(
                new
                {
                    library,
                    path = result.Document.Path,
                    title = result.Document.Title,
                    contentHash = result.Document.ContentHash,
                    source = info.Source,
                    editable = info.Editable,
                    origin = info.Origin,
                    originEdit = info.OriginEdit,
                }
            );
            return $"status: exists\nsaved: false\n{details}\n<instruction_for_agents>Read it or choose a different, more specific path.</instruction_for_agents>";
        }
        return $"status: rejected\ncode: {result.Status.ToCode()}\nsaved: false";
    }
}
