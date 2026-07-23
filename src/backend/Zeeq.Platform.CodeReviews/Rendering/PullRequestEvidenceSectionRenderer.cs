namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders supporting review evidence such as raw findings XML.
/// </summary>
public sealed class PullRequestEvidenceSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestEvidence;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        if (kind is not (GitHubCommentKinds.ReviewCompleted or GitHubCommentKinds.StubReviewCompleted))
        {
            return null;
        }

        if (
            string.IsNullOrWhiteSpace(context.FindingsXml)
            || context.Findings is null
            || context.Findings.NoAgentsActivated
            || context.Findings.Reviews.Sum(review => review.Findings.Count) == 0
        )
        {
            return Remove();
        }

        return new(
            SectionKind,
            OrderKey: null,
            GitHubCommentPatchMode.ReplaceSection,
            RenderRawXml(context.FindingsXml)
        );
    }

    private static string RenderRawXml(string findingsXml)
    {
        return $"""
            <details>
            <summary>❮EXPAND❯ Raw review findings XML for agents</summary>

            Use a planning or brainstorming skill to work with your agent on this feedback.

            ````xml
            <!--
            <instruction_for_agents>
            The following XML is the raw output from expert code reviewers analyzing the PR.
            - Review each finding in the feedback from the expert reviewers
            - Evaluate the validity of each finding in the broader context of the codebase; the reviewer only saw the PR contents and not the broader codebase; determine the veracity of each finding
            - The change proposals are high level; plan out **specific code changes** needed to implement the feedback, finding-by-finding
            - ALWAYS get confirmation and acceptance of the proposed fix for each finding before writing code
            - ALWAYS ensure there is enough clarity to make the best fix if there is ambiguity or insufficient feedback to confidently implement the change
            - Call out any tradeoffs or shortcomings in the proposed changes if any (especially in the broader context of the codebase)
            - Present the concrete changes needed and let the user decide which to proceed with; do not make changes without confirmation
            - Use a checklist/todo list to keep track of work against this set of findings; we do not want to wing it here
            - If a behavior is expected or by design, suggest leaving a comment near the code to explain reasoning to future travelers (agents)
            - Add code comment: "NOTE: (Reason to defer or ignore a finding goes here)" to document any rationale for deferring or ignoring a finding
            </instruction_for_agents>
            -->

            {EscapeFence(findingsXml)}
            ````
            </details>
            """.TrimEnd();
    }

    private static string EscapeFence(string value) =>
        value.Replace("````", "````\u200B", StringComparison.Ordinal);

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);
}
