using System.Diagnostics;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest;

/// <summary>Resolves Notion page ancestry into Zeeq's normalized document-path identity.</summary>
internal sealed class NotionPagePathResolver(IZeeqNotionClient client)
{
    internal const int MaxDepth = 100;
    internal const int MaxPathLength = 2048;

    private readonly Dictionary<string, NotionPage> _nodes = new(StringComparer.Ordinal);

    /// <summary>Seeds page metadata already returned by full-search discovery.</summary>
    internal void Seed(NotionPageSummary page) =>
        _nodes[page.PageId] = new NotionPage(page.PageId, page.Title, page.Parent);

    /// <summary>
    /// Resolves one page. Returns <see langword="null"/> only when the leaf itself is absent;
    /// a missing ancestor is a path-resolution failure because the leaf still exists.
    /// </summary>
    internal async Task<ResolvedNotionPage?> ResolveAsync(
        string pageId,
        CancellationToken cancellationToken
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.notion.path_resolve",
            ActivityKind.Internal,
            parentContext: default,
            tags: [new("notion.page.id", pageId)]
        );

        var cacheHits = 0;
        var cacheMisses = 0;
        var depth = 0;
        var currentPageId = pageId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<string>();
        string? leafTitle = null;

        try
        {
            while (currentPageId is not null)
            {
                if (depth >= MaxDepth)
                {
                    throw new InvalidOperationException(
                        $"Notion page path exceeded the maximum depth of {MaxDepth}."
                    );
                }

                if (!visited.Add(currentPageId))
                {
                    throw new InvalidOperationException(
                        $"Notion page path contains an ancestor cycle at '{currentPageId}'."
                    );
                }

                NotionPage? page;
                if (_nodes.TryGetValue(currentPageId, out var cached))
                {
                    cacheHits++;
                    page = cached;
                }
                else
                {
                    cacheMisses++;
                    page = await client.GetPageAsync(currentPageId, cancellationToken);
                    if (page is not null)
                    {
                        _nodes[currentPageId] = page;
                    }
                }

                if (page is null)
                {
                    if (depth == 0)
                    {
                        return null;
                    }

                    throw new InvalidOperationException(
                        $"Notion ancestor '{currentPageId}' is unavailable while resolving page '{pageId}'."
                    );
                }

                leafTitle ??= page.Title;
                segments.Add(SlugSegment(page.Title));
                currentPageId = page.Parent.ParentPageId;
                depth++;
            }

            segments.Reverse();
            var path = BuildMarkdownPath(segments);
            if (path.Length > MaxPathLength)
            {
                throw new InvalidOperationException(
                    $"Notion page path exceeds the maximum stored length of {MaxPathLength} characters."
                );
            }

            return new ResolvedNotionPage(pageId, leafTitle ?? "Untitled", path);
        }
        finally
        {
            activity?.SetTag("path.depth", depth);
            activity?.SetTag("cache.hits", cacheHits);
            activity?.SetTag("cache.misses", cacheMisses);
        }
    }

    private static string BuildMarkdownPath(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return "/untitled.md";
        }

        var fileName = $"{segments[^1]}.md";

        return segments.Count == 1
            ? $"/{fileName}"
            : $"/{string.Join('/', segments.Take(segments.Count - 1))}/{fileName}";
    }

    private static string SlugSegment(string title)
    {
        var slug = new StringBuilder();
        var previousWasSeparator = false;

        foreach (var character in title.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator && slug.Length > 0)
            {
                slug.Append('-');
                previousWasSeparator = true;
            }
        }

        if (slug.Length > 0 && slug[^1] == '-')
        {
            slug.Length--;
        }

        return slug.Length == 0 ? "untitled" : slug.ToString();
    }
}

/// <summary>Resolved page identity used by the Notion ingest runner.</summary>
internal sealed record ResolvedNotionPage(string PageId, string Title, string Path);
