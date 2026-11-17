using System.Text.Json;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents.Editing;

/// <summary>Builds safe, agent-facing document ownership metadata.</summary>
public static class DocumentAuthoringMetadata
{
    /// <summary>Serializes metadata separately from the markdown body.</summary>
    public static string Render(string library, AuthoringDocument document)
    {
        var (source, editable, origin, originEdit) = Describe(document);
        var json = JsonSerializer.Serialize(
            new
            {
                library,
                path = document.Path,
                contentHash = document.ContentHash,
                source,
                editable,
                origin,
                originEdit,
                originPath = source == "GitHub"
                    ? (HasOriginalGitHubPath(document) ? "source" : "normalized_best_effort")
                    : null,
            }
        );
        return $"<zeeq_document_meta>\n{json}\n</zeeq_document_meta>";
    }

    /// <summary>Whether the original case-preserving GitHub path is available.</summary>
    public static bool HasOriginalGitHubPath(AuthoringDocument document) =>
        !string.IsNullOrWhiteSpace(document.Local?.SourceOrigin?.SourcePath);

    /// <summary>Returns source ownership and safe links where available.</summary>
    public static (string Source, bool Editable, string? Origin, string? OriginEdit) Describe(
        AuthoringDocument document
    )
    {
        var local = document.Local;
        var sourceOrigin = local?.SourceOrigin;
        var synced =
            document.Public is not null
            || local?.Authoring == LibraryDocumentAuthoring.Synced
            || local?.SourceExternalId is not null
            || sourceOrigin is not null;
        if (!synced)
            return (local!.Authoring.ToString(), true, null, null);
        var source = document.Public is not null
            ? "GitHub"
            : sourceOrigin?.Kind ?? (local?.SourceExternalId is not null ? "Notion" : "Synced");
        if (string.Equals(source, "Notion", StringComparison.OrdinalIgnoreCase))
            return (
                "Notion",
                false,
                Guid.TryParse(local?.SourceExternalId, out var id)
                    ? $"https://www.notion.so/{id:N}"
                    : null,
                null
            );
        var repo = document.PublicRepoUrl ?? sourceOrigin?.RepoRef;
        if (
            !string.Equals(source, "GitHub", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(repo, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        )
            return (source, false, null, null);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return ("GitHub", false, null, null);
        var repoName = Uri.UnescapeDataString(segments[1]);
        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];
        var owner = Uri.UnescapeDataString(segments[0]);
        if (
            owner.Length == 0
            || repoName.Length == 0
            || owner.Contains('/')
            || repoName.Contains('/')
        )
            return ("GitHub", false, null, null);
        var root =
            $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repoName)}";
        var sourcePath = sourceOrigin?.SourcePath ?? document.Path;
        var pathSegments = sourcePath.Replace('\\', '/').Trim('/').Split('/');
        if (pathSegments.Any(segment => segment is "" or "." or ".."))
            return ("GitHub", false, null, null);
        var file = string.Join('/', pathSegments.Select(Uri.EscapeDataString));
        return ("GitHub", false, $"{root}/blob/HEAD/{file}", $"{root}/edit/HEAD/{file}");
    }
}
