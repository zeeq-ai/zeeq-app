namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Finding-level counts produced by one code-review execution.
/// </summary>
/// <remarks>
/// Shared between the sync agent runner (<see cref="ExpertCodeReviewRunner"/>) and the
/// async PR runner (<see cref="CodeReviewRunner"/>) so both can produce consistent
/// aggregate counts from a <see cref="CodeReviewOutputDocument"/> without duplicating
/// the counting logic.
/// </remarks>
internal sealed record CodeReviewFindingCounts(
    int Critical = 0,
    int Major = 0,
    int Minor = 0,
    int Suggestion = 0,
    int Comment = 0
);

/// <summary>
/// Extension members for <see cref="CodeReviewOutputDocument"/>.
/// </summary>
internal static class CodeReviewOutputDocumentExtensions
{
    extension(CodeReviewOutputDocument output)
    {
        /// <summary>
        /// Counts findings by level across every review facet in this output.
        /// </summary>
        public CodeReviewFindingCounts CountFindings()
        {
            var counts = new CodeReviewFindingCounts();

            foreach (var finding in output.Reviews.SelectMany(review => review.Findings))
            {
                counts = finding.Level switch
                {
                    CodeReviewFindingLevel.Critical => counts with { Critical = counts.Critical + 1 },
                    CodeReviewFindingLevel.Major => counts with { Major = counts.Major + 1 },
                    CodeReviewFindingLevel.Minor => counts with { Minor = counts.Minor + 1 },
                    CodeReviewFindingLevel.Suggestion => counts with
                    {
                        Suggestion = counts.Suggestion + 1,
                    },
                    CodeReviewFindingLevel.Comment => counts with { Comment = counts.Comment + 1 },
                    _ => counts,
                };
            }

            return counts;
        }
    }
}
