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
            GitHubCommentKinds.DraftPrompt =>
                "PR is currently a draft. Zeeq will review it when it is ready.",
            GitHubCommentKinds.Ignored => "This GitHub event did not require a new Zeeq review.",
            GitHubCommentKinds.AlreadyRunning => "A Zeeq review is already running for this PR.",
            GitHubCommentKinds.AllowanceExhausted =>
                "The review budget for this PR is exhausted.",
            GitHubCommentKinds.Queued =>
                "Zeeq accepted this PR for review and queued the reviewer workflow.",
            GitHubCommentKinds.ReviewFailed => RenderFailed(context),
            GitHubCommentKinds.NoAgentsActivated =>
                "No configured reviewer agents matched the files in this PR.",
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
        kind
            is GitHubCommentKinds.ReviewCompleted
                or GitHubCommentKinds.StubReviewCompleted
                or GitHubCommentKinds.NoAgentsActivated;

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);
}
