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

    [Test]
    [Arguments("Cache-key normalization", true)]
    [Arguments("Different claim", false)]
    public async Task FixtureJudge_HandlesRejectedReferenceOn903(string title, bool matches)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var judge = new CodeReviewEvaluationFixtureJudge(new(env, [("org", "repo")]), null!);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodeReviewEvaluationSnapshot(
            1,
            "org",
            "repo",
            "owner/repo",
            903,
            "title",
            "body",
            "base",
            "head",
            "author",
            "https://example.test",
            false,
            PullRequestState.Closed,
            now,
            1,
            []
        );
        var reference = new CodeReviewEvaluationReferenceFinding(
            "r",
            title,
            "summary",
            CodeReviewEvaluationFindingSeverity.Minor,
            "src/Search.cs",
            null,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            "user",
            now,
            CodeReviewEvaluationReferenceDisposition.Rejected
        );
        var candidate = new CodeReviewEvaluationCandidate(
            "c",
            "Cache-key normalization",
            "details",
            CodeReviewFindingLevel.Minor,
            "src/Search.cs",
            24
        );
        var result = await judge.GradeAsync(
            "org",
            null!,
            snapshot,
            [reference],
            [candidate],
            CancellationToken.None
        );
        await Assert.That(result.Grade.Matches.Count).IsEqualTo(matches ? 1 : 0);
    }
}
