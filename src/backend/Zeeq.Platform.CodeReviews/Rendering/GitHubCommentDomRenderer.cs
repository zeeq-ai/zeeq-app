using System.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Applies section patches to a current DOM and renders the complete GitHub comment body.
/// </summary>
/// <remarks>
/// The renderer preserves existing sections by default. It first applies the
/// message-level clear list, then runs section renderers against that post-clear
/// DOM, then sorts the resulting sections by rank before writing Markdown.
/// </remarks>
public sealed class GitHubCommentDomRenderer(
    IEnumerable<IGitHubCommentSectionRenderer> sectionRenderers
) : IGitHubCommentDomRenderer
{
    private readonly IReadOnlyList<IGitHubCommentSectionRenderer> _sectionRenderers =
        sectionRenderers.ToArray();

    /// <inheritdoc />
    public string Render(
        string kind,
        IReadOnlyList<string> clear,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        var sections = currentDom.Sections.ToList();
        foreach (var marker in clear)
        {
            sections.RemoveAll(section => section.Marker == marker);
        }

        var renderDom = currentDom.WithSections(sections);
        foreach (var renderer in _sectionRenderers)
        {
            var patch = renderer.Render(kind, context, renderDom);
            if (patch is null)
            {
                continue;
            }

            ApplyPatch(sections, patch);
        }

        return RenderBody(currentDom.RootMarker, sections);
    }

    private static void ApplyPatch(
        List<GitHubCommentDomSection> sections,
        GitHubCommentDomPatch patch
    )
    {
        var index = sections.FindIndex(section => section.Marker == patch.SectionKind);
        if (patch.Mode == GitHubCommentPatchMode.RemoveSection)
        {
            if (index >= 0)
            {
                sections.RemoveAt(index);
            }

            return;
        }

        if (patch.Mode == GitHubCommentPatchMode.InsertIfMissing && index >= 0)
        {
            return;
        }

        var orderKey =
            patch.OrderKey
            ?? (index >= 0 ? sections[index].OrderKey : null)
            ?? ResolveDefaultOrderKey(patch.SectionKind);
        var section = new GitHubCommentDomSection
        {
            Marker = patch.SectionKind,
            OrderKey = orderKey,
            Content = patch.Markdown ?? string.Empty,
        };

        if (index >= 0)
        {
            sections[index] = section;
            return;
        }

        sections.Add(section);
    }

    private static string ResolveDefaultOrderKey(string sectionKind)
    {
        if (GitHubCommentMarkers.DefaultOrderKeys.TryGetValue(sectionKind, out var orderKey))
        {
            return orderKey;
        }

        throw new InvalidOperationException(
            $"Section '{sectionKind}' must provide an order key because no default rank is configured."
        );
    }

    private static string RenderBody(
        string rootMarker,
        IReadOnlyList<GitHubCommentDomSection> sections
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<!-- ({GitHubCommentMarkers.RootOrderKey}):{rootMarker}:start -->");

        foreach (
            var section in sections.OrderBy(section => section.OrderKey, StringComparer.Ordinal)
        )
        {
            sb.AppendLine($"<!-- ({section.OrderKey}):{section.Marker}:start -->");
            sb.AppendLine(section.Content.Trim());
            sb.AppendLine($"<!-- {section.Marker}:end -->");
            sb.AppendLine();
        }

        sb.Append($"<!-- {rootMarker}:end -->");
        return sb.ToString();
    }
}
