using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public class CodeReviewEvaluationComparisonTests
{
    [Test]
    public async Task ComparesOnlyPairedGradedCasesAndLeavesUnknownRatesNull()
    {
        CodeReviewEvaluationCaseResult Row(
            string id,
            int matched,
            int expected,
            int repeated,
            CodeReviewEvaluationCaseOutcome outcome = CodeReviewEvaluationCaseOutcome.Graded
        ) =>
            new()
            {
                Id = id,
                OrganizationId = "org",
                RunId = "run",
                ManifestCaseId = id,
                PullRequestNumber = 1,
                Phase = CodeReviewEvaluationCasePhase.Graded,
                Outcome = outcome,
                MatchedReferenceCount = matched,
                ExpectedReferenceCount = expected,
                RepeatedFalsePositiveCount = repeated,
            };
        var comparison = CodeReviewEvaluationComparisons.Compare(
            [Row("a", 2, 4, 2), Row("skipped", 0, 4, 0, CodeReviewEvaluationCaseOutcome.Skipped)],
            [Row("a", 3, 4, 1), Row("unpaired", 0, 4, 0)]
        );
        await Assert.That(comparison.PairedCaseCount).IsEqualTo(1);
        await Assert.That(comparison.RecallDelta).IsEqualTo(0.25);
        await Assert.That(comparison.KnownPrecisionDelta).IsEqualTo(0.25);
        var fpOnly = CodeReviewEvaluationComparisons.Compare(
            [Row("fp", 0, 0, 1, CodeReviewEvaluationCaseOutcome.FalsePositiveChecked)],
            [Row("fp", 0, 0, 0, CodeReviewEvaluationCaseOutcome.FalsePositiveChecked)]
        );
        await Assert.That(fpOnly.RecallDelta).IsNull();
        var empty = CodeReviewEvaluationComparisons.Compare([], []);
        await Assert.That(empty.RecallDelta).IsNull();
        await Assert.That(empty.KnownPrecisionDelta).IsNull();
    }
}
