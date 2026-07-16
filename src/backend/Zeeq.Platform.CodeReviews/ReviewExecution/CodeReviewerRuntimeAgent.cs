using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Per-run reviewer configuration resolved from persisted agents or the built-in fallback reviewer.
/// </summary>
/// <remarks>
/// This type intentionally carries no provider key, credential reference, or model name. Phase 4 resolves
/// <see cref="ModelTier" /> through organization LLM settings or system defaults immediately before creating
/// the Agent Framework runtime agent.
/// </remarks>
/// <param name="Id">Stable reviewer id for this run. Built-in reviewers use an in-code id.</param>
/// <param name="DisplayName">Human-readable reviewer name for comments and logs.</param>
/// <param name="ReviewFacet">Facet label owned by this reviewer.</param>
/// <param name="ModelTier">Semantic Zeeq model tier to resolve at execution time.</param>
/// <param name="Prompt">Reviewer-specific instructions.</param>
/// <param name="ActivationConfiguration">File activation rules for this reviewer.</param>
/// <param name="IsFallbackDefault">True when this reviewer came from the built-in fallback factory.</param>
public sealed record CodeReviewerRuntimeAgent(
    string Id,
    string DisplayName,
    string ReviewFacet,
    CodeReviewModelTier ModelTier,
    string Prompt,
    CodeReviewerActivationConfiguration ActivationConfiguration,
    bool IsFallbackDefault = false
)
{
    /// <summary>
    /// Composes the final user prompt for this reviewer: its identity header, then the shared
    /// reviewer-neutral body, then its previous-reviews footer.
    /// </summary>
    /// <remarks>
    /// Lives here because the <c>&lt;identity&gt;</c> block is framed from this reviewer's own
    /// <see cref="DisplayName"/> and <see cref="ReviewFacet"/>, and the caller
    /// (<see cref="CodeReviewReviewerValidatingExecutor"/>) already holds the reviewer instance.
    /// Composition runs at execution time — "construct the final string just as we execute the
    /// prompt" — so the agent system prompt stays byte-stable across runs and the LLM prompt cache
    /// can be reused.
    ///
    /// Example output:
    /// <code>
    /// &lt;identity&gt;
    ///   &lt;name use_verbatim&gt;Security Reviewer&lt;/name&gt;
    ///   &lt;facet use_verbatim&gt;Security&lt;/facet&gt;
    /// &lt;/identity&gt;
    ///
    /// Apply your expert review to code in this PR ... (shared body)
    ///
    /// &lt;previous_reviews&gt; ... &lt;/previous_reviews&gt;
    /// </code>
    /// </remarks>
    /// <param name="sharedPullRequestPromptBody">
    /// The reviewer-neutral body from <see cref="CodeReviewUserPrompt.SharedPullRequestPromptBody"/>.
    /// Identical for every reviewer in a run.
    /// </param>
    /// <param name="previousReviewsSection">
    /// Pre-rendered <c>&lt;previous_reviews&gt;</c> XML for this reviewer's facet, or empty when none.
    /// Produced by <see cref="CodeReviewAgentExecutor.BuildPreviousReviewsSection"/>.
    /// </param>
    public string ComposeUserPrompt(
        string sharedPullRequestPromptBody,
        string previousReviewsSection
    ) =>
        $"""
            <identity>
              <name use_verbatim>{DisplayName}</name>
              <facet use_verbatim>{ReviewFacet}</facet>
            </identity>

            {sharedPullRequestPromptBody}

            {previousReviewsSection}
            """;
}
