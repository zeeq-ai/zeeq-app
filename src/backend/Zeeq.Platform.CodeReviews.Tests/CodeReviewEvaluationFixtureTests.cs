using System.Security.Claims;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationFixtureTests
{
    [Test]
    public async Task FixtureIsDevelopmentOnlyAndUsesExactTenantRepositoryPairs()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        await Assert
            .That(() => new CodeReviewEvaluationFixtureRepositories(env, [("org", "repo")]))
            .Throws<InvalidOperationException>();
        env.EnvironmentName.Returns("Development");
        var fixtures = new CodeReviewEvaluationFixtureRepositories(env, [("org", "repo")]);
        await Assert.That(fixtures.Contains("org", "repo")).IsTrue();
        await Assert.That(fixtures.Contains("other", "repo")).IsFalse();
        await Assert.That(fixtures.Contains("org", "repo_other")).IsFalse();
    }

    [Test]
    public async Task UnknownFixtureCaseFailsClosedBeforeRealReviewer()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var fixtures = new CodeReviewEvaluationFixtureRepositories(env, [("org", "repo")]);
        var reviewer = new CodeReviewEvaluationFixtureReviewer(fixtures, null!);
        var model = new CodeReviewEvaluationModelSelection(
            CodeReviewModelTier.High,
            "provider",
            "model",
            null,
            CodeReviewEvaluationCredentialSource.SystemDefault,
            null
        );
        var agent = new CodeReviewerRuntimeAgent(
            "agent",
            "name",
            "facet",
            CodeReviewModelTier.High,
            "prompt",
            new()
        );
        async Task Act() =>
            _ = await reviewer.ExecuteAsync(
                "org",
                [agent],
                false,
                new(""),
                [],
                new ClaimsPrincipal(),
                new CodeReviewTelemetryContext("org", "repo"),
                CodeReviewExecutionOptions.Evaluation(model) with
                {
                    FixturePullRequestNumber = 999,
                },
                CancellationToken.None
            );
        await Assert.That(Act).Throws<InvalidOperationException>();
    }
}
