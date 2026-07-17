using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// The shared, reviewer-neutral body of a code-review user prompt, plus the rule for building it
/// from a <see cref="CodeReviewPromptInput"/>.
/// </summary>
/// <remarks>
/// Built once per run by <see cref="From"/> and broadcast to every reviewer. <see cref="From"/> is
/// deliberately side-effect free: it formats already-resolved records, source snapshots, feedback,
/// and file scopes; it must not fetch GitHub data, read storage, query the KB, or touch the database.
///
/// The per-reviewer <c>&lt;identity&gt;</c> header and <c>&lt;previous_reviews&gt;</c> footer used to
/// live in the agent system prompt (see git history of
/// <see cref="CodeReviewAgentExecutor.BuildAgentSystemInstructions"/>); they were moved out so the
/// system prompt is byte-stable across runs and the LLM prompt cache can be reused. That per-reviewer
/// composition now lives on <see cref="CodeReviewerRuntimeAgent.ComposeUserPrompt"/> and runs in
/// <see cref="CodeReviewReviewerValidatingExecutor"/> — "construct the final string just as we
/// execute the prompt".
/// </remarks>
/// <param name="SharedPullRequestPromptBody">
/// Reviewer-neutral prompt body (guidelines, finding levels, libraries, PR title/description,
/// diff). Identical for every reviewer in a run.
/// </param>
public sealed record CodeReviewUserPrompt(string SharedPullRequestPromptBody)
{
    /// <summary>
    /// Builds the shared, reviewer-neutral prompt body for one code review execution context.
    /// </summary>
    /// <remarks>
    /// The result becomes <see cref="SharedPullRequestPromptBody"/> — the reviewer-neutral body
    /// broadcast identically to every reviewer in a run. It does not include the per-reviewer
    /// <c>&lt;identity&gt;</c> header or <c>&lt;previous_reviews&gt;</c> footer; those are appended at
    /// execution time by <see cref="CodeReviewReviewerValidatingExecutor"/> via
    /// <see cref="CodeReviewerRuntimeAgent.ComposeUserPrompt"/>.
    ///
    /// Additional prompt parts are in
    /// <c>src/backend/Zeeq.Platform.CodeReviews/ReviewExecution/CodeReviewOutputPrompt.cs</c>.
    /// </remarks>
    public static CodeReviewUserPrompt From(CodeReviewPromptInput input)
    {
        var prDescription = string.IsNullOrWhiteSpace(input.Body)
            ? "(No description.)"
            : input.Body.Trim();

        // Comments extracted from the GitHub PR targeted to +zeeq, or /zeeq.
        var developerFeedback = RenderDeveloperFeedback(input.DeveloperFeedbackComments);

        // Files in this PR but out of scope (filtered out); shown so the agent knows what else changed.
        var excludedFiles = BuildOutOfScopeFileList(input.OutOfScopeFiles);

        // The diff buffer of the PR for the in-scope files.
        var diffBuffer = BuildInScopeDiff(input.InScopeFiles);

        // Libraries mapped to this repository that can be used when calling the tools.
        var librariesSection = BuildLibrariesSection(input.LibraryNames);

        // Repository-configured shared guidance (if any) for cross-cutting prompt behavior.
        var sharedGuidanceSection = BuildSharedGuidanceSection(input.SharedPromptFragment);

        return new CodeReviewUserPrompt(
            $"""
            <remember_very_important_key_instructions>
            1. tool_usage
            2. concise_targeted_feedback
            3. do_not_overwhelm
            4. avoid_speculation
            5. apply_finding_levels_appropriately
            6. critical_json_output_rules
            7. use_of_previous_reviews
            </remember_very_important_key_instructions>

            {librariesSection}

            {sharedGuidanceSection}

            <pr_title>
            {input.Title}
            </pr_title>

            <pr_description>
            {prDescription}
            </pr_description>

            {developerFeedback}

            <pr_other_files>
            {excludedFiles}
            </pr_other_files>

            <pr_diff>
            <!-- [ BEGIN CHANGES IN CURRENT PR ] -->
            {diffBuffer}
            <!-- [ END CHANGES IN CURRENT PR ] -->
            </pr_diff>
            """
        );
    }

    private static string BuildLibrariesSection(IReadOnlyList<string> libraryNames)
    {
        if (libraryNames.Count == 0)
        {
            return "<library_names />";
        }

        var items = string.Join("\n", libraryNames.Select(name => $"- {name}"));

        return $"""
            <library_names>
            {items}
            </library_names>
            """;
    }

    /// <summary>
    /// Renders organization/repository-wide guidance shared identically across every
    /// reviewer agent for this run.
    /// </summary>
    private static string BuildSharedGuidanceSection(string sharedPromptFragment)
    {
        if (string.IsNullOrWhiteSpace(sharedPromptFragment))
        {
            return "<organization_guidance />";
        }

        return $"""
            <organization_guidance>
            {sharedPromptFragment.Trim()}
            </organization_guidance>
            """;
    }

    private static string RenderDeveloperFeedback(
        IReadOnlyList<CodeReviewDeveloperFeedbackComment> comments
    )
    {
        if (comments.Count == 0)
        {
            return "<developer_feedback />";
        }

        var section = new XElement(
            "developer_feedback",
            comments
                .OrderBy(comment => comment.CreatedAtUtc)
                .ThenBy(comment => comment.AuthorLogin, StringComparer.Ordinal)
                .Select(comment => new XElement(
                    "comment",
                    new XAttribute("author_login", comment.AuthorLogin),
                    new XAttribute(
                        "created_at_utc",
                        comment
                            .CreatedAtUtc.ToUniversalTime()
                            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    ),
                    new XElement("body", comment.Body)
                ))
        );

        return section.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildOutOfScopeFileList(IReadOnlyList<CodeReviewFileSnapshot> files)
    {
        var excludedFiles = new StringBuilder();

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            excludedFiles.AppendLine(
                $"""<file name="{file.Path}" status="{file.MutationState}" />"""
            );
        }

        return excludedFiles.ToString();
    }

    private static string BuildInScopeDiff(IReadOnlyList<CodeReviewFileSnapshot> files)
    {
        var diffBuffer = new StringBuilder();

        var index = 0;

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var patch = string.IsNullOrWhiteSpace(file.Patch)
                ? "(No text patch.)"
                : file.Patch.TrimEnd();

            diffBuffer.Append(
                $"""

                <file_patch name="{file.Path}" status="{file.MutationState}" item="{index} of {files.Count}">
                {patch}
                </file_patch>

                """
            );

            index++;
        }

        return diffBuffer.ToString();
    }
}
