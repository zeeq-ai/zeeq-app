using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Paired evaluation metrics. Precision counts only labelled expected and rejected matches.</summary>
public sealed record CodeReviewEvaluationComparison(
    int PairedCaseCount,
    double? BaselineRecall,
    double? ExperimentRecall,
    double? RecallDelta,
    double? BaselineKnownPrecision,
    double? ExperimentKnownPrecision,
    double? KnownPrecisionDelta,
    int BaselineRepeatedFalsePositives,
    int ExperimentRepeatedFalsePositives
);

/// <summary>Calculates metrics on cases graded or checked for false positives in both runs.</summary>
public static class CodeReviewEvaluationComparisons
{
    /// <summary>Compares paired results without treating unlabelled findings as false positives.</summary>
    public static CodeReviewEvaluationComparison Compare(
        IReadOnlyList<CodeReviewEvaluationCaseResult> baseline,
        IReadOnlyList<CodeReviewEvaluationCaseResult> experiment
    )
    {
        var byId = baseline
            .Where(IsGraded)
            .ToDictionary(x => x.ManifestCaseId, StringComparer.Ordinal);
        var pairs = experiment
            .Where(IsGraded)
            .Where(x => byId.ContainsKey(x.ManifestCaseId))
            .Select(x => (Baseline: byId[x.ManifestCaseId], Experiment: x))
            .ToArray();
        var a = pairs.Select(x => x.Baseline).ToArray();
        var b = pairs.Select(x => x.Experiment).ToArray();
        double? Rate(int numerator, int denominator) =>
            denominator == 0 ? null : (double)numerator / denominator;
        var expectedPairs = pairs
            .Where(x =>
                x.Baseline.Outcome == CodeReviewEvaluationCaseOutcome.Graded
                && x.Experiment.Outcome == CodeReviewEvaluationCaseOutcome.Graded
            )
            .ToArray();
        var ar = Rate(
            expectedPairs.Sum(x => x.Baseline.MatchedReferenceCount),
            expectedPairs.Sum(x => x.Baseline.ExpectedReferenceCount)
        );
        var br = Rate(
            expectedPairs.Sum(x => x.Experiment.MatchedReferenceCount),
            expectedPairs.Sum(x => x.Experiment.ExpectedReferenceCount)
        );
        var ap = Rate(
            a.Sum(x => x.MatchedReferenceCount),
            a.Sum(x => x.MatchedReferenceCount + x.RepeatedFalsePositiveCount)
        );
        var bp = Rate(
            b.Sum(x => x.MatchedReferenceCount),
            b.Sum(x => x.MatchedReferenceCount + x.RepeatedFalsePositiveCount)
        );
        return new(
            pairs.Length,
            ar,
            br,
            br - ar,
            ap,
            bp,
            bp - ap,
            a.Sum(x => x.RepeatedFalsePositiveCount),
            b.Sum(x => x.RepeatedFalsePositiveCount)
        );
    }

    private static bool IsGraded(CodeReviewEvaluationCaseResult result) =>
        result.Outcome
            is CodeReviewEvaluationCaseOutcome.Graded
                or CodeReviewEvaluationCaseOutcome.FalsePositiveChecked;
}
