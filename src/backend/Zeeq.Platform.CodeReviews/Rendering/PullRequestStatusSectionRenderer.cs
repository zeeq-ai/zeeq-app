namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders transient or failure status text for the PR summary comment.
/// </summary>
public sealed class PullRequestStatusSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestStatus;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        if (IsCompletedKind(kind) && string.IsNullOrWhiteSpace(context.FindingsLoadError))
        {
            return Remove();
        }

        var markdown = kind switch
        {
            "draft_prompt" => "PR is currently a draft. Zeeq will review it when it is ready.",
            "ignored" => "This GitHub event did not require a new Zeeq review.",
            "already_running" => "A Zeeq review is already running for this PR.",
            "allowance_exhausted" => "The review budget for this PR is exhausted.",
            "queued" => "Zeeq accepted this PR for review and queued the reviewer workflow.",
            "review_failed" => RenderFailed(context),
            "no_agents_activated" => "No configured reviewer agents matched the files in this PR.",
            _ when !string.IsNullOrWhiteSpace(context.FindingsLoadError) =>
                $"Review completed, but Zeeq could not load the findings artifact: {context.FindingsLoadError}",
            _ => null,
        };

        return string.IsNullOrWhiteSpace(markdown)
            ? null
            : new(SectionKind, OrderKey: null, GitHubCommentPatchMode.ReplaceSection, markdown);
    }

    private static string RenderFailed(CodeReviewCommentRenderContext context)
    {
        var failure = GitHubCommentMarkdown.SanitizeModelMarkdown(context.Review?.FailureMessage);

        return string.IsNullOrWhiteSpace(failure)
            ? "Zeeq review failed before findings were produced."
            : $"Zeeq review failed before findings were produced.\n\n```text\n{failure}\n```";
    }

    private static bool IsCompletedKind(string kind) =>
        kind is "review_completed" or "stub_review_completed" or "no_agents_activated";

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);
}
