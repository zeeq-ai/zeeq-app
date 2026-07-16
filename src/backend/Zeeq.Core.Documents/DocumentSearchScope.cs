namespace Zeeq.Core.Documents;

/// <summary>
/// Scoped marker that tells document stores which execution context their queries serve.
/// </summary>
/// <remarks>
/// Registered as a scoped service with a default of <c>false</c>, so every ordinary request
/// scope (interactive MCP server, HTTP endpoints, background workers) queries unfiltered.
/// The code-review tool path is the only writer: <c>ScopedServiceAIFunction</c> creates a
/// fresh child scope per reviewer tool invocation and marks this instance before the tool
/// runs, so <c>PostgresLibraryDocumentStore</c> and <c>PostgresLibraryDocumentSnippetStore</c>
/// hide documents flagged <see cref="LibraryDocument.ExcludedFromCodeReviews"/> from list and
/// search results on that path only.
/// <para>
/// This is deliberately a scoped DI holder rather than an <see cref="AsyncLocal{T}"/> ambient
/// (the <c>ToolTelemetrySink</c> pattern) because the review path already owns a
/// per-invocation DI seam and the flag is a correctness concern: making it an explicit
/// constructor dependency of the stores keeps the behavior visible in signatures and trivially
/// testable. Direct path resolution (<c>GetByPathAsync</c>) intentionally ignores this scope —
/// an excluded document must still resolve when a reviewer reads it by path.
/// </para>
/// </remarks>
public sealed class DocumentSearchScope
{
    /// <summary>
    /// True when the current DI scope serves a code-review agent tool invocation; document
    /// stores then hide <see cref="LibraryDocument.ExcludedFromCodeReviews"/> documents from
    /// list and search results.
    /// </summary>
    public bool ForCodeReviewExecution { get; set; }
}
