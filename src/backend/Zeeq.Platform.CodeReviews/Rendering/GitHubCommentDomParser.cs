namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Parses Zeeq-managed GitHub comment Markdown into a one-level DOM.
/// </summary>
/// <remarks>
/// The parser intentionally supports one root marker and one level of direct
/// child sections. Different marker-looking text inside a child section is
/// treated as section-owned Markdown, but a nested start marker for the same
/// section is malformed and the section is rejected.
/// </remarks>
public static class GitHubCommentDomParser
{
    /// <summary>
    /// Parses a GitHub comment body into a DOM for a target.
    /// </summary>
    /// <param name="target">Logical GitHub comment target.</param>
    /// <param name="body">GitHub issue or review comment body.</param>
    /// <returns>An empty DOM when the body is blank or does not contain a Zeeq root marker.</returns>
    public static GitHubCommentDom Parse(GitHubCommentTargetSelector target, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return GitHubCommentDom.Empty(target);
        }

        if (!TryFindStartMarker(body, startIndex: 0, rootOnly: true, out var rootStart))
        {
            return GitHubCommentDom.Empty(target);
        }

        if (rootStart.Marker != GitHubCommentMarkers.RootFor(target))
        {
            return GitHubCommentDom.Empty(target);
        }

        var rootEnd = FindEndMarker(body, rootStart.Marker, rootStart.EndIndex);
        if (rootEnd < 0)
        {
            return GitHubCommentDom.Empty(target);
        }

        var rootContent = body[rootStart.EndIndex..rootEnd];
        var sections = ParseSections(rootContent);

        return new GitHubCommentDom(target, rootStart.Marker, sections);
    }

    /// <summary>
    /// Checks whether a GitHub comment body contains the root marker for the target.
    /// </summary>
    /// <remarks>
    /// The resolver uses this before parsing during marker scans. A PR can have
    /// more than one Zeeq-owned comment, so finding any Zeeq root marker is
    /// not enough. The marker must match the exact logical target being resolved.
    /// </remarks>
    public static bool ContainsRootForTarget(GitHubCommentTargetSelector target, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return TryFindStartMarker(body, startIndex: 0, rootOnly: true, out var rootStart)
            && rootStart.Marker == GitHubCommentMarkers.RootFor(target);
    }

    private static IReadOnlyList<GitHubCommentDomSection> ParseSections(string rootContent)
    {
        var sections = new List<GitHubCommentDomSection>();
        var searchIndex = 0;

        while (searchIndex < rootContent.Length)
        {
            if (!TryFindStartMarker(rootContent, searchIndex, rootOnly: false, out var start))
            {
                break;
            }

            if (start.Marker.EndsWith("-root", StringComparison.Ordinal))
            {
                searchIndex = start.EndIndex;
                continue;
            }

            var end = FindEndMarker(rootContent, start.Marker, start.EndIndex);
            if (end < 0)
            {
                break;
            }

            if (HasSameMarkerStartBeforeEnd(rootContent, start.Marker, start.EndIndex, end))
            {
                // NOTE: Same-marker nesting is not a supported escape hatch. Treat that section as
                // malformed instead of guessing which end marker the writer intended to own it.
                searchIndex = end + EndMarker(start.Marker).Length;
                continue;
            }

            sections.Add(
                new GitHubCommentDomSection
                {
                    Marker = start.Marker,
                    OrderKey = start.OrderKey,
                    Content = rootContent[start.EndIndex..end],
                }
            );
            searchIndex = end + EndMarker(start.Marker).Length;
        }

        return sections;
    }

    private static bool TryFindStartMarker(
        string text,
        int startIndex,
        bool rootOnly,
        out ParsedStartMarker marker
    )
    {
        var searchIndex = startIndex;
        while (searchIndex < text.Length)
        {
            var commentStart = text.IndexOf("<!--", searchIndex, StringComparison.Ordinal);
            if (commentStart < 0)
            {
                break;
            }

            var commentEnd = text.IndexOf("-->", commentStart, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                break;
            }

            if (
                TryParseStartComment(
                    text[(commentStart + 4)..commentEnd].Trim(),
                    commentStart,
                    commentEnd + 3,
                    out marker
                ) && (!rootOnly || marker.Marker.EndsWith("-root", StringComparison.Ordinal))
            )
            {
                return true;
            }

            searchIndex = commentEnd + 3;
        }

        marker = default;
        return false;
    }

    private static bool TryParseStartComment(
        string comment,
        int startIndex,
        int endIndex,
        out ParsedStartMarker marker
    )
    {
        marker = default;
        if (!comment.StartsWith('('))
        {
            return false;
        }

        var orderEnd = comment.IndexOf("):", StringComparison.Ordinal);
        if (orderEnd < 0)
        {
            return false;
        }

        var markerText = comment[(orderEnd + 2)..];
        const string StartSuffix = ":start";
        if (!markerText.EndsWith(StartSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        marker = new ParsedStartMarker(
            OrderKey: comment[1..orderEnd],
            Marker: markerText[..^StartSuffix.Length],
            StartIndex: startIndex,
            EndIndex: endIndex
        );
        return true;
    }

    private static bool HasSameMarkerStartBeforeEnd(
        string text,
        string marker,
        int startIndex,
        int endIndex
    )
    {
        var searchIndex = startIndex;
        while (TryFindStartMarker(text, searchIndex, rootOnly: false, out var nestedStart))
        {
            if (nestedStart.StartIndex >= endIndex)
            {
                return false;
            }

            if (nestedStart.Marker == marker)
            {
                return true;
            }

            searchIndex = nestedStart.EndIndex;
        }

        return false;
    }

    private static int FindEndMarker(string text, string marker, int startIndex) =>
        text.IndexOf(EndMarker(marker), startIndex, StringComparison.Ordinal);

    private static string EndMarker(string marker) => $"<!-- {marker}:end -->";

    private readonly record struct ParsedStartMarker(
        string OrderKey,
        string Marker,
        int StartIndex,
        int EndIndex
    );
}
