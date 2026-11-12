using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Unit tests for reviewer revision content hashing.
/// </summary>
/// <remarks>
/// The hash is the revision identity. It must include every stored behavior-bearing field so two
/// rows can never share an identity while holding different configuration, and it must be stable
/// so an unchanged save does not create revision noise.
/// </remarks>
public sealed class CodeReviewerAgentRevisionContentTests
{
    [Test]
    public async Task ComputeHash_ChangesWhenEnabledToggles()
    {
        var enabled = Configuration(enabled: true);
        var disabled = Configuration(enabled: false);

        var enabledHash = CodeReviewerAgentRevisionContent.ComputeHash(enabled);
        var disabledHash = CodeReviewerAgentRevisionContent.ComputeHash(disabled);

        await Assert.That(enabledHash).IsNotEqualTo(disabledHash);
    }

    [Test]
    public async Task ComputeHash_IsStableForIdenticalContent()
    {
        var first = CodeReviewerAgentRevisionContent.ComputeHash(Configuration(enabled: true));
        var second = CodeReviewerAgentRevisionContent.ComputeHash(Configuration(enabled: true));

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task CaptureThenDeserialize_ProducesTheSameHash()
    {
        var agent = new CodeReviewerAgent
        {
            Id = "cra_1",
            OrganizationId = "org_1",
            RepositoryId = "repo_1",
            DisplayName = "Boundary reviewer",
            ReviewFacet = "Correctness",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review for correctness.",
            Enabled = false,
            ActivationConfiguration = new CodeReviewerActivationConfiguration(),
        };

        var captured = CodeReviewerAgentRevisionContent.Capture(agent);
        var stored = CodeReviewerAgentRevisionContent.Serialize(captured);
        var restored = CodeReviewerAgentRevisionContent.Deserialize(stored);

        await Assert.That(restored.Enabled).IsFalse();
        await Assert
            .That(CodeReviewerAgentRevisionContent.ComputeHash(restored))
            .IsEqualTo(CodeReviewerAgentRevisionContent.ComputeHash(captured));
    }

    private static CodeReviewerAgentRevisionConfiguration Configuration(bool enabled) =>
        new(
            "Boundary reviewer",
            "Correctness",
            CodeReviewModelTier.High,
            "Review for correctness.",
            enabled,
            new CodeReviewerActivationConfiguration()
        );
}
