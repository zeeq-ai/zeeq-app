using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;

namespace Zeeq.Data.Postgres.Tests;

public class CodeReviewEvaluationReferenceValidationTests
{
    private static readonly CodeReviewEvaluationReferenceFinding Finding = new(
        "ref",
        "title",
        "summary",
        CodeReviewEvaluationFindingSeverity.Minor,
        "file.cs",
        null,
        CodeReviewEvaluationReferenceOrigin.Manual,
        null,
        null,
        "user",
        DateTimeOffset.UnixEpoch
    );

    [Test]
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled)]
    [Arguments(CodeReviewEvaluationReferenceState.ReviewedClean)]
    public async Task NonExpectedStates_AcceptRejectedButRejectExpected(
        CodeReviewEvaluationReferenceState state
    )
    {
        var set = new CodeReviewEvaluationReferenceSet(
            "agent",
            state,
            [Finding with { Disposition = CodeReviewEvaluationReferenceDisposition.Rejected }]
        );
        await Assert.That(CodeReviewEvaluationCaseValidation.ValidateReferenceSets([set])).IsNull();
        await Assert.That(set.RequiresJudge).IsTrue();
        await Assert
            .That(
                CodeReviewEvaluationCaseValidation.ValidateReferenceSets([
                    set with
                    {
                        Findings = [Finding],
                    },
                ])
            )
            .IsEqualTo("reference_state_conflict");
    }

    [Test]
    public async Task ReferenceLimitsAndDisposition_AreValidated()
    {
        var set = new CodeReviewEvaluationReferenceSet(
            "agent",
            CodeReviewEvaluationReferenceState.Expected,
            [Finding]
        );
        await Assert
            .That(
                CodeReviewEvaluationCaseValidation.ValidateReferenceSets([
                    set with
                    {
                        Findings = Enumerable
                            .Range(0, 101)
                            .Select(i => Finding with { Id = i.ToString() })
                            .ToArray(),
                    },
                ])
            )
            .IsEqualTo("reference_limit_reached");
        await Assert
            .That(
                CodeReviewEvaluationCaseValidation.ValidateReferenceSets([
                    set with
                    {
                        Findings = [Finding with { Rationale = new string('x', 1001) }],
                    },
                ])
            )
            .IsEqualTo("reference_rationale_too_long");
        await Assert
            .That(
                CodeReviewEvaluationCaseValidation.ValidateReferenceSets([
                    set with
                    {
                        Findings =
                        [
                            Finding with
                            {
                                Disposition = (CodeReviewEvaluationReferenceDisposition)42,
                            },
                        ],
                    },
                ])
            )
            .IsEqualTo("reference_disposition_invalid");
    }
}
