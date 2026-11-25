using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

public class CodeReviewEvaluationReferenceSetSerializerTests
{
    [Test]
    public async Task LegacyAndRejectedReferences_RoundTrip()
    {
        const string legacy = """
            [{"reviewerAgentId":"agent","state":"Expected","findings":[{"id":"ref","title":"title","summary":"summary","severity":"Minor","file":"file.cs","line":null,"origin":"Manual","sourceRunId":null,"sourceFindingId":null,"approvedByUserId":"user","approvedAtUtc":"1970-01-01T00:00:00+00:00"}]}]
            """;
        var sets = CodeReviewEvaluationReferenceSetSerializer.Deserialize(legacy);
        var finding = sets.Single().Findings.Single();
        await Assert
            .That(finding.Disposition)
            .IsEqualTo(CodeReviewEvaluationReferenceDisposition.Expected);
        await Assert.That(finding.Rationale).IsNull();
        var rejected = sets[0] with
        {
            Findings =
            [
                finding with
                {
                    Disposition = CodeReviewEvaluationReferenceDisposition.Rejected,
                    Rationale = "Incorrect claim",
                },
            ],
        };
        var roundTrip = CodeReviewEvaluationReferenceSetSerializer
            .Deserialize(CodeReviewEvaluationReferenceSetSerializer.Serialize([rejected]))
            .Single()
            .Findings.Single();
        await Assert
            .That(roundTrip.Disposition)
            .IsEqualTo(CodeReviewEvaluationReferenceDisposition.Rejected);
        await Assert.That(roundTrip.Rationale).IsEqualTo("Incorrect claim");
    }
}
