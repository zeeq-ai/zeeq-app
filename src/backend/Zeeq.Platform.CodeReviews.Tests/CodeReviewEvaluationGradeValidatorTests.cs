namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationGradeValidatorTests
{
    [Test]
    public async Task ValidMatchesDeriveMissesAndUnverifiedCandidates()
    {
        var grade = CodeReviewEvaluationGradeValidator.Validate(
            """{"matches":[{"referenceId":"r1","candidateId":"c1","reason":"same issue"}]}""",
            ["r1", "r2"],
            ["c1", "c2"]
        );
        await Assert.That(grade.Matches.Count).IsEqualTo(1);
        await Assert.That(grade.MissedReferenceIds.Single()).IsEqualTo("r2");
        await Assert.That(grade.AdditionalCandidateIds.Single()).IsEqualTo("c2");
    }

    [Test]
    [Arguments(
        """{"matches":[{"referenceId":"r1","candidateId":"c1","reason":"x"},{"referenceId":"r2","candidateId":"c1","reason":"x"}]}"""
    )]
    [Arguments("""{"matches":[{"referenceId":"unknown","candidateId":"c1","reason":"x"}]}""")]
    [Arguments("""{"matches":null}""")]
    [Arguments("""not json""")]
    public async Task InvalidJudgeOutputFails(string json)
    {
        await Assert
            .That(() => CodeReviewEvaluationGradeValidator.Validate(json, ["r1", "r2"], ["c1"]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyCandidateMeansAllReferencesMissed()
    {
        var grade = CodeReviewEvaluationGradeValidator.Validate("""{"matches":[]}""", ["r1"], []);
        await Assert.That(grade.MissedReferenceIds.Single()).IsEqualTo("r1");
        await Assert.That(grade.AdditionalCandidateIds).IsEmpty();
    }
}
