namespace Zeeq.Core.Common;

/// <summary>
/// Ambient, best-effort sink for tool knowledge-source attribution.
/// </summary>
/// <remarks>
/// The MCP document tools (<c>Zeeq.Mcp.Documents</c>) call <see cref="RecordSource" />
/// and <see cref="RecordMissedQuery" /> unconditionally after surfacing rows; both are a
/// no-op unless a scope is active. The real MCP server never opens a scope, so the tools
/// carry zero telemetry cost there. A code-review run opens a scope (via its
/// <c>CodeReviewTelemetryMiddleware</c>) so the same tool calls attribute their sources to
/// the run.
///
/// The scope flows through <see cref="AsyncLocal{T}" /> — the same ambient model as
/// <c>Activity.Current</c>. Failures are swallowed so telemetry can never change tool
/// behavior or output. This is the neutral home next to <see cref="ZeeqTelemetry" />
/// because both <c>Zeeq.Mcp.Documents</c> and <c>Zeeq.Platform.CodeReviews</c> already
/// reference <c>Zeeq.Core.Common</c>.
/// </remarks>
public static class ToolTelemetrySink
{
    private static readonly AsyncLocal<IToolTelemetrySink?> CurrentSink = new();

    /// <summary>Gets the sink active on the current async flow, or null when none is set.</summary>
    public static IToolTelemetrySink? Current => CurrentSink.Value;

    /// <summary>
    /// Sets the ambient sink for the returned scope's lifetime, restoring the previous sink on dispose.
    /// </summary>
    /// <param name="sink">The sink that receives sources recorded inside the scope.</param>
    /// <returns>A disposable that restores the previously active sink.</returns>
    public static IDisposable BeginScope(IToolTelemetrySink sink)
    {
        var previous = CurrentSink.Value;
        CurrentSink.Value = sink;

        return new RestoreScope(installed: sink, previous: previous);
    }

    /// <summary>Records one surfaced source with the active sink; best-effort no-op otherwise.</summary>
    /// <param name="source">The source hit to record.</param>
    public static void RecordSource(ToolKnowledgeSource source)
    {
        try
        {
            CurrentSink.Value?.RecordSource(source);
        }
        catch
        {
            // Best-effort: telemetry must never affect tool execution or output.
        }
    }

    /// <summary>Records a search that returned zero rows (content-gap signal). Best-effort no-op.</summary>
    /// <param name="toolName">The MCP tool that produced no rows, e.g. <c>search_sections</c>.</param>
    /// <param name="query">The query that returned nothing.</param>
    public static void RecordMissedQuery(string toolName, string query)
    {
        try
        {
            CurrentSink.Value?.RecordMissedQuery(toolName, query);
        }
        catch
        {
            // Best-effort: telemetry must never affect tool execution or output.
        }
    }

    /// <summary>
    /// Restores the previously active sink when a scope is disposed, but only when the sink it
    /// installed is still the active one.
    /// </summary>
    /// <remarks>
    /// Scopes are opened via <c>using</c> and therefore dispose in LIFO order on an isolated
    /// <see cref="AsyncLocal{T}" /> flow, for which prior-value restore alone is already correct.
    /// The identity guard additionally prevents an out-of-order or accidental double dispose from
    /// clobbering a newer nested sink and misrouting later telemetry to the wrong collector.
    /// </remarks>
    /// <param name="installed">The sink this scope set as current.</param>
    /// <param name="previous">The sink to restore, i.e. whatever was current before this scope.</param>
    private sealed class RestoreScope(IToolTelemetrySink installed, IToolTelemetrySink? previous)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Only unwind when our sink is still current; if a newer nested scope is active
            // (out-of-order disposal), leave it in place rather than clobbering it.
            if (ReferenceEquals(CurrentSink.Value, installed))
            {
                CurrentSink.Value = previous;
            }
        }
    }
}

/// <summary>
/// Receives tool knowledge-source attributions from the document tools during a scoped run.
/// </summary>
/// <remarks>
/// Implemented by the code-review run collector. The sink is invoked through
/// <see cref="ToolTelemetrySink" />, so implementations only see calls while their scope is
/// active. Implementations must be safe to call from concurrent tool invocations.
/// </remarks>
public interface IToolTelemetrySink
{
    /// <summary>Records one surfaced source (a document, section, or code sample hit).</summary>
    /// <param name="source">The source hit reported by a document tool.</param>
    void RecordSource(ToolKnowledgeSource source);

    /// <summary>Records a search that returned zero rows, attributed to the current facet by the collector.</summary>
    /// <param name="toolName">The MCP tool that produced no rows.</param>
    /// <param name="query">The query that returned nothing.</param>
    void RecordMissedQuery(string toolName, string query);
}

/// <summary>
/// One transient source hit reported by a document tool, aggregated later into the review snapshot.
/// </summary>
/// <remarks>
/// Each surfaced row carries BOTH the snippet (<paramref name="Heading" />/<paramref name="Language" />)
/// and its owning document (<paramref name="DocumentPath" />/<paramref name="DocumentTitle" />), so
/// document importance can aggregate from snippet hits. The stable
/// <paramref name="DocumentId" />/<paramref name="SnippetId" /> support offline ETL joins across
/// document revisions where headings/paths drift.
/// </remarks>
/// <param name="ToolName">MCP tool name, e.g. <c>search_sections</c>.</param>
/// <param name="Kind">Whether the hit is an Index, Document, Section, or CodeSample.</param>
/// <param name="Usage">How the source was used: Listed, Searched, or Read.</param>
/// <param name="Library">Library name searched or read.</param>
/// <param name="DocumentPath">Owning document path (current, resolved at read time).</param>
/// <param name="DocumentTitle">Owning document title.</param>
/// <param name="Heading">Snippet heading path; null for document-level hits.</param>
/// <param name="Language">Fence language for code snippets; else null.</param>
/// <param name="Query">Search query that surfaced this source; null for reads.</param>
/// <param name="DocumentId">Stable owning-document id (for ETL joins); null when unknown.</param>
/// <param name="SnippetId">Stable snippet id (for ETL joins); null for document-level hits.</param>
/// <param name="Rank">Best 1-based rank of this hit across search arms; 0 when not ranked (reads).</param>
/// <param name="Score">Fused relevance score for this hit; 0 when not ranked.</param>
/// <param name="IdentifierMatch">Whether the query's identifiers overlapped this row.</param>
public sealed record ToolKnowledgeSource(
    string ToolName,
    ToolKnowledgeSourceKind Kind,
    ToolKnowledgeSourceUsage Usage,
    string? Library = null,
    string? DocumentPath = null,
    string? DocumentTitle = null,
    string? Heading = null,
    string? Language = null,
    string? Query = null,
    string? DocumentId = null,
    string? SnippetId = null,
    int Rank = 0,
    double Score = 0,
    bool IdentifierMatch = false
);

/// <summary>The kind of knowledge source a tool surfaced.</summary>
public enum ToolKnowledgeSourceKind
{
    /// <summary>A library or document index listing (e.g. <c>list_documents</c>).</summary>
    Index,

    /// <summary>A whole document (e.g. <c>search_documents</c> or <c>read_document_by_path</c>).</summary>
    Document,

    /// <summary>A prose section snippet (e.g. <c>search_sections</c>).</summary>
    Section,

    /// <summary>A code sample snippet (e.g. <c>search_code_snippets</c>).</summary>
    CodeSample,
}

/// <summary>How a knowledge source was used by the tool call.</summary>
public enum ToolKnowledgeSourceUsage
{
    /// <summary>Enumerated in an index listing without being opened.</summary>
    Listed,

    /// <summary>Surfaced as a ranked search result.</summary>
    Searched,

    /// <summary>Opened and read directly by path.</summary>
    Read,
}
