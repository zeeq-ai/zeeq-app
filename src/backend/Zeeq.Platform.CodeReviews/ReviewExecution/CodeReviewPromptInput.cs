namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Minimal side-effect-free input required to build a code-review prompt.
/// </summary>
/// <remarks>
/// GitHub-backed reviews obtain this shape from <see cref="CodeReviewExecutionContext.ToPromptInput" />
/// after the runner has loaded the pull-request snapshot, developer feedback, and repository file
/// scope. MCP uploaded-diff reviews construct it directly from <see cref="ExpertCodeReviewRunRequest" />
/// plus the parsed uploaded diff. <see cref="CodeReviewUserPrompt.From" /> consumes this record without
/// additional I/O so prompt rendering stays deterministic and testable.
/// </remarks>
/// <param name="Title">
/// Review title used in the prompt's <c>&lt;pr_title&gt;</c> block. For GitHub reviews this comes from
/// <see cref="CodeReviewPullRequestSnapshot.Title" />; for MCP uploaded-diff reviews it comes from
/// <see cref="ExpertCodeReviewRunRequest.Title" /> and falls back to the owner-qualified repository
/// name when no title is supplied. It frames the review subject for the agent.
/// </param>
/// <param name="Body">
/// Review description used in the prompt's <c>&lt;pr_description&gt;</c> block. For GitHub reviews this
/// comes from <see cref="CodeReviewPullRequestSnapshot.Body" />; for MCP uploaded-diff reviews it
/// comes from <see cref="ExpertCodeReviewRunRequest.Description" /> and falls back to an empty string.
/// The prompt builder trims it and renders <c>(No description.)</c> when it is blank.
/// </param>
/// <param name="DeveloperFeedbackComments">
/// Prior reviewer or developer feedback rendered into the prompt's <c>&lt;developer_feedback&gt;</c>
/// section. GitHub reviews populate this from
/// <see cref="CodeReviewPullRequestSnapshot.DeveloperFeedbackComments" /> so agents can avoid
/// repeating already-addressed context and can account for human discussion. MCP uploaded-diff
/// reviews currently pass an empty list, which renders as a self-closing feedback section.
/// </param>
/// <param name="InScopeFiles">
/// File snapshots rendered as <c>&lt;file_patch&gt;</c> blocks and therefore directly reviewed by the
/// agents. GitHub reviews receive these after repository file filters are applied to the pull-request
/// snapshot; MCP uploaded-diff reviews receive them from <see cref="GitDiffParser" /> and
/// <see cref="UploadedDiffFileMapping" />, optionally filtered by mapped repository configuration.
/// Only these files contribute patch bodies to the prompt.
/// </param>
/// <param name="OutOfScopeFiles">
/// File snapshots excluded by repository file filters and rendered only as the prompt's
/// <c>&lt;pr_other_files&gt;</c> list. They tell agents what else changed without exposing patch bodies,
/// which keeps reviews focused on configured file scopes while preserving broader PR context.
/// MCP uploaded-diff reviews use an empty list when the repository is not mapped in Zeeq.
/// </param>
/// <param name="LibraryNames">
/// List of valid library names mapped to the repository that the code review is
/// associated with.  These libraries are used by the review agents via tools.  MCP
/// uploaded-diff reviews use an empty list when the repository is not mapped in
/// Zeeq.
/// </param>
/// <param name="SharedPromptFragment">
/// Organization-authored guidance from <see cref="Zeeq.Core.Models.CodeRepositoryReviewConfiguration"/>,
/// rendered into the prompt's <c>&lt;organization_guidance&gt;</c> block so every reviewer
/// agent sees it identically. Empty renders a self-closing tag. MCP uploaded-diff
/// reviews use an empty string when the repository is not mapped in Zeeq.
/// </param>
public sealed record CodeReviewPromptInput(
    string Title,
    string Body,
    IReadOnlyList<CodeReviewDeveloperFeedbackComment> DeveloperFeedbackComments,
    IReadOnlyList<CodeReviewFileSnapshot> InScopeFiles,
    IReadOnlyList<CodeReviewFileSnapshot> OutOfScopeFiles,
    IReadOnlyList<string> LibraryNames,
    string SharedPromptFragment = ""
);
