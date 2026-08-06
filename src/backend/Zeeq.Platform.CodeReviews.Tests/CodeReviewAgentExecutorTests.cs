using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests for <see cref="CodeReviewAgentExecutor"/> instruction building.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewAgentExecutorTests/*"
/// </summary>
public sealed class CodeReviewAgentExecutorTests
{
    [Test]
    public async Task BuildAgentSystemInstructions_IsStaticAndDoesNotContainDynamicContent()
    {
        var reviewer = new CodeReviewerRuntimeAgent(
            Id: "agent_1",
            DisplayName: "Test Reviewer",
            ReviewFacet: "Security",
            ModelTier: CodeReviewModelTier.High,
            Prompt: "Review for security.",
            ActivationConfiguration: CodeReviewerActivationConfiguration.Empty
        );

        var instructions = CodeReviewAgentExecutor.BuildAgentSystemInstructions(reviewer);

        await Assert.That(instructions).Contains("<tool_usage>");
        await Assert.That(instructions).DoesNotContain("valid_libraries=\"lib-");
        await Assert.That(instructions).DoesNotContain("<identity>");
        await Assert.That(instructions).DoesNotContain("<name use_verbatim>");
        await Assert.That(instructions).DoesNotContain("<previous_reviews>");
    }

    [Test]
    public async Task ComposeUserPrompt_WithReviewerAndPreviousReviews_ProducesOrderedContent()
    {
        var reviewer = new CodeReviewerRuntimeAgent(
            Id: "agent_security",
            DisplayName: "Security Reviewer",
            ReviewFacet: "Security",
            ModelTier: CodeReviewModelTier.High,
            Prompt: "Review for security issues.",
            ActivationConfiguration: CodeReviewerActivationConfiguration.Empty
        );
        const string sharedPullRequestPromptBody = "Apply your expert review.";
        const string previousReviews =
            "<previous_reviews><review><summary>Old finding.</summary></review></previous_reviews>";

        var composed = reviewer.ComposeUserPrompt(sharedPullRequestPromptBody, previousReviews);

        await Assert.That(composed).Contains("<identity>");
        await Assert.That(composed).Contains("<name use_verbatim>Security Reviewer</name>");
        await Assert.That(composed).Contains("<facet use_verbatim>Security</facet>");
        await Assert.That(composed).Contains(sharedPullRequestPromptBody);
        await Assert.That(composed).Contains(previousReviews);

        // Identity must come before the shared body, which must come before previous reviews.
        var identityIndex = composed.IndexOf("<identity>", StringComparison.Ordinal);
        var bodyIndex = composed.IndexOf(sharedPullRequestPromptBody, StringComparison.Ordinal);
        var previousIndex = composed.IndexOf("<previous_reviews>", StringComparison.Ordinal);

        await Assert.That(identityIndex).IsLessThan(bodyIndex);
        await Assert.That(bodyIndex).IsLessThan(previousIndex);
    }
}
