using Zeeq.Core.Models;

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

    [Test]
    [Arguments("./src/Search.cs")]
    [Arguments("/src/Search.cs")]
    [Arguments("src\\Search.cs")]
    public async Task RejectedMatch_RequiresNormalizedSameFile(string file)
    {
        var refs = new[]
        {
            new CodeReviewEvaluationReferenceTarget(
                "r",
                CodeReviewEvaluationReferenceDisposition.Rejected,
                "src/Search.cs"
            ),
        };
        var grade = CodeReviewEvaluationGradeValidator.Validate(
            """{"matches":[{"referenceId":"r","candidateId":"c","reason":"same claim"}]}""",
            refs,
            [new("c", file)]
        );
        await Assert.That(grade.RepeatedRejectedReferenceIds.Single()).IsEqualTo("r");
        await Assert
            .That(grade.Matches.Single().Disposition)
            .IsEqualTo(CodeReviewEvaluationReferenceDisposition.Rejected);
        await Assert.That(grade.AdditionalCandidateIds).IsEmpty();
        var crossFile = CodeReviewEvaluationGradeValidator.Validate(
            """{"matches":[{"referenceId":"r","candidateId":"c","reason":"same claim"}]}""",
            refs,
            [new("c", "src/Other.cs")]
        );
        await Assert.That(crossFile.CrossFileRejectedMatchesFiltered).IsEqualTo(1);
        await Assert.That(crossFile.AvoidedRejectedReferenceIds.Single()).IsEqualTo("r");
        await Assert.That(crossFile.AdditionalCandidateIds.Single()).IsEqualTo("c");
    }

    [Test]
    public async Task CandidateCannotMatchBothDispositions()
    {
        var refs = new[]
        {
            new CodeReviewEvaluationReferenceTarget(
                "e",
                CodeReviewEvaluationReferenceDisposition.Expected,
                "src/A.cs"
            ),
            new CodeReviewEvaluationReferenceTarget(
                "r",
                CodeReviewEvaluationReferenceDisposition.Rejected,
                "src/A.cs"
            ),
        };
        await Assert
            .That(() =>
                CodeReviewEvaluationGradeValidator.Validate(
                    """{"matches":[{"referenceId":"e","candidateId":"c","reason":"x"},{"referenceId":"r","candidateId":"c","reason":"x"}]}""",
                    refs,
                    [new("c", "src/A.cs")]
                )
            )
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Judge_EmptyCandidates_DerivesMissesAndAvoidedWithoutProvider()
    {
        var finding = new CodeReviewEvaluationReferenceFinding(
            "r",
            "title",
            "summary",
            CodeReviewEvaluationFindingSeverity.Minor,
            "src/A.cs",
            null,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            "u",
            DateTimeOffset.UnixEpoch,
            CodeReviewEvaluationReferenceDisposition.Rejected
        );
        var judge = new CodeReviewEvaluationJudge(null!);
        var result = await judge.GradeAsync(
            "org",
            null!,
            null!,
            [finding],
            [],
            CancellationToken.None
        );
        await Assert.That(result.Grade.AvoidedRejectedReferenceIds.Single()).IsEqualTo("r");
        await Assert.That(result.InputTokens).IsNull();
    }
}
