using System.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders the collapsed "documents and sources consulted" section of the PR review comment.
/// </summary>
/// <remarks>
/// Driven by the persisted <see cref="CodeReviewSourceTelemetry" /> (deserialized by the comment
/// handler), this section answers "how did the reviewers reach this conclusion" — so it renders for
/// any completed review kind <b>regardless of finding count</b>, including clean (zero-finding) and
/// no-agents runs. It is removed only when there is no telemetry to show.
///
/// Ports v1's <c>ReviewOutput</c> telemetry block, but as a document-centric section: a documents
/// table ranked by hit count, per-document snippet bullets, a tool-usage table, and a content-gap
/// (missed-query) list. Table cells are escaped like v1's <c>Table()</c> helper. The snapshot is
/// already importance-ordered and hard-capped; this renderer additionally trims to a comment-
/// friendly top-N (the UI panel shows the full, already-capped set).
///
/// NOTE: <c>review_failed</c> is intentionally excluded — a failed comment stays focused on the
/// failure message; any partial telemetry captured before the failure remains in storage and the
/// API for debugging.
/// </remarks>
public sealed class PullRequestSourcesSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <summary>Documents shown in the comment table before trimming the least-important tail.</summary>
    private const int MaxDocuments = 25;

    /// <summary>Snippet bullets shown per document before trimming.</summary>
    private const int MaxSnippetsPerDocument = 5;

    /// <summary>Missed queries shown in the content-gap list before trimming.</summary>
    private const int MaxMissedQueries = 10;

    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestSources;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        // Show for any completed review (incl. zero findings / no agents) so readers can see how
        // the conclusion was reached; other kinds (queued/running/failed) leave the section alone.
        if (
            kind
            is not (
                GitHubCommentKinds.ReviewCompleted
                or GitHubCommentKinds.StubReviewCompleted
                or GitHubCommentKinds.NoAgentsActivated
            )
        )
        {
            return null;
        }

        var telemetry = context.SourceTelemetry;
        if (telemetry is null || telemetry.IsEmpty)
        {
            return Remove();
        }

        return new(
            SectionKind,
            OrderKey: null,
            GitHubCommentPatchMode.ReplaceSection,
            RenderMarkdown(telemetry)
        );
    }

    private static string RenderMarkdown(CodeReviewSourceTelemetry telemetry)
    {
        var body = new StringBuilder();

        body.AppendLine("<details>");
        body.AppendLine($"<summary>{RenderSummaryLine(telemetry.Summary)}</summary>");
        body.AppendLine();

        if (telemetry.Documents.Count > 0)
        {
            AppendDocuments(body, telemetry.Documents);
        }

        if (telemetry.ToolUsage.Count > 0)
        {
            AppendToolUsage(body, telemetry.ToolUsage);
        }

        if (telemetry.MissedQueries.Count > 0)
        {
            AppendMissedQueries(body, telemetry.MissedQueries);
        }

        body.AppendLine("</details>");

        return body.ToString().TrimEnd();
    }

    private static string RenderSummaryLine(CodeReviewSourceSummary summary) =>
        $"❮EXPAND❯ 📚 Documents & sources consulted "
        + $"({summary.DocumentCount} docs, {summary.SnippetCount} snippets, {summary.ToolCallCount} tool calls)";

    private static void AppendDocuments(
        StringBuilder body,
        IReadOnlyList<CodeReviewSourceDocument> documents
    )
    {
        body.AppendLine("### Documents consulted");
        body.AppendLine();
        body.AppendLine("| Document | Hits | Read | Facets |");
        body.AppendLine("| --- | ---: | :---: | --- |");

        var shown = documents.Take(MaxDocuments).ToArray();

        foreach (var document in shown)
        {
            // Use a dash (not blank) for "not read" so an empty cell doesn't read as missing data.
            var readMarker = document.ReadAfterSearch ? "✓" : "—";
            body.AppendLine(
                $"| {Code($"zeeq://{document.Path.TrimStart('/')}")} | {document.HitCount} | {readMarker} | {EscapeText(JoinList(document.Facets))} |"
            );
        }

        if (documents.Count > shown.Length)
        {
            body.AppendLine();
            body.AppendLine(
                $"_+{documents.Count - shown.Length} more documents (see the review UI)._"
            );
        }

        body.AppendLine();

        // Per-document snippet breakdown, only for documents that surfaced snippets.
        foreach (var document in shown.Where(document => document.Snippets.Count > 0))
        {
            body.AppendLine(
                $"**{EscapeText(document.Title)}** — {Code($"zeeq://{document.Path.TrimStart('/')}")}"
            );

            foreach (var snippet in document.Snippets.Take(MaxSnippetsPerDocument))
            {
                // Kind is a controlled enum name (Section/CodeSample), safe to emit raw inside the
                // superscript; the fence language is KB/agent-derived so it is still escaped.
                var descriptor = snippet.Language is { Length: > 0 } language
                    ? $"{snippet.Kind} {EscapeText(language)}"
                    : snippet.Kind;
                body.AppendLine(
                    $"- {EscapeText(TrimLeadingHeadingSegment(snippet.Heading))} <sup>{descriptor}</sup>"
                );
            }

            if (document.Snippets.Count > MaxSnippetsPerDocument)
            {
                body.AppendLine($"- _+{document.Snippets.Count - MaxSnippetsPerDocument} more…_");
            }

            body.AppendLine();
        }
    }

    private static void AppendToolUsage(
        StringBuilder body,
        IReadOnlyList<CodeReviewToolUsage> toolUsage
    )
    {
        body.AppendLine("### Tool usage");
        body.AppendLine();
        body.AppendLine("| Tool | Calls | OK | Failed |");
        body.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var usage in toolUsage)
        {
            body.AppendLine(
                $"| {EscapeText(usage.Tool)} | {usage.Calls} | {usage.Succeeded} | {usage.Failed} |"
            );
        }

        body.AppendLine();
    }

    private static void AppendMissedQueries(
        StringBuilder body,
        IReadOnlyList<CodeReviewMissedQuery> missedQueries
    )
    {
        body.AppendLine("### Content gaps");
        body.AppendLine();
        body.AppendLine("Searches that returned nothing (candidate documentation to add):");
        body.AppendLine();

        foreach (var miss in missedQueries.Take(MaxMissedQueries))
        {
            body.AppendLine($"- {Code(miss.Query)} — {EscapeText(miss.Tool)}");
        }

        if (missedQueries.Count > MaxMissedQueries)
        {
            body.AppendLine($"- _+{missedQueries.Count - MaxMissedQueries} more…_");
        }

        body.AppendLine();
    }

    private static string JoinList(IReadOnlyList<string> values) => string.Join(", ", values);

    /// <summary>
    /// Drops the leading segment of a <c>" > "</c>-joined heading path, keeping the more specific
    /// tail. The first segment is the document's top heading, already shown as the bold document
    /// label above the bullets, so repeating it on every snippet is noise. A single-segment heading
    /// (no separator) is returned unchanged.
    /// </summary>
    private static string TrimLeadingHeadingSegment(string heading)
    {
        const string Separator = " > ";
        var index = heading.IndexOf(Separator, StringComparison.Ordinal);

        return index >= 0 ? heading[(index + Separator.Length)..] : heading;
    }

    /// <summary>
    /// Escapes a value for a Markdown text position (table cell, list item, bold/emphasis).
    /// </summary>
    /// <remarks>
    /// Telemetry text (document titles, snippet headings, tool names, facets, fence languages) is
    /// KB- or agent-derived and can legitimately contain Markdown-significant characters — a real
    /// KB heading is <c>Functional Programming, `Action`, `Func&lt;T&gt;`</c>. Backslash-escaping the
    /// inline-significant set keeps such values from distorting the comment layout while rendering
    /// them verbatim. Newlines collapse to spaces so a value cannot break out of a table row.
    /// </remarks>
    private static string EscapeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var normalized = value.ReplaceLineEndings(" ");
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>Wraps a value in an inline code span that untrusted text cannot break out of.</summary>
    /// <remarks>
    /// Inside an inline code span all characters render literally, so only a backtick can terminate
    /// it early; neutralizing backticks (and collapsing newlines) makes the span safe for
    /// agent-supplied queries and document paths.
    /// </remarks>
    private static string Code(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var body = value.ReplaceLineEndings(" ").Replace("`", "'", StringComparison.Ordinal);

        return $"`{body}`";
    }

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);
}
