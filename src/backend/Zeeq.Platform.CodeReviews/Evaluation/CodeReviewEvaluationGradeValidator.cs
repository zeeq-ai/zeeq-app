using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>A one-to-one judge assignment and its explanation.</summary>
public sealed record CodeReviewEvaluationMatch(
    string ReferenceId,
    string CandidateId,
    string Reason
);

/// <summary>Validated binary assignments. Unmatched candidates are unverified.</summary>
public sealed record CodeReviewEvaluationGrade(
    IReadOnlyList<CodeReviewEvaluationMatch> Matches,
    IReadOnlyList<string> MissedReferenceIds,
    IReadOnlyList<string> AdditionalCandidateIds
);

/// <summary>Rejects malformed or inflated judge output before it is persisted.</summary>
public static class CodeReviewEvaluationGradeValidator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record JudgeResponse(List<CodeReviewEvaluationMatch>? Matches);

    /// <summary>Validates identifiers and derives misses and unverified additional findings.</summary>
    public static CodeReviewEvaluationGrade Validate(
        string json,
        IReadOnlyList<string> referenceIds,
        IReadOnlyList<string> candidateIds
    )
    {
        var references = referenceIds.ToHashSet(StringComparer.Ordinal);
        var candidates = candidateIds.ToHashSet(StringComparer.Ordinal);
        if (
            references.Count != referenceIds.Count
            || candidates.Count != candidateIds.Count
            || referenceIds.Any(string.IsNullOrWhiteSpace)
            || candidateIds.Any(string.IsNullOrWhiteSpace)
        )
            throw new InvalidOperationException("Finding identifiers must be unique and nonempty.");

        JudgeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<JudgeResponse>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Judge output is invalid JSON.", ex);
        }
        if (response?.Matches is null)
            throw new InvalidOperationException("Judge output is missing matches.");

        var matchedReferences = new HashSet<string>(StringComparer.Ordinal);
        var matchedCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in response.Matches)
        {
            if (
                match is null
                || string.IsNullOrWhiteSpace(match.ReferenceId)
                || string.IsNullOrWhiteSpace(match.CandidateId)
                || string.IsNullOrWhiteSpace(match.Reason)
                || !references.Contains(match.ReferenceId)
                || !candidates.Contains(match.CandidateId)
                || !matchedReferences.Add(match.ReferenceId)
                || !matchedCandidates.Add(match.CandidateId)
            )
                throw new InvalidOperationException(
                    "Judge output contains an invalid or duplicate match."
                );
        }

        return new(
            response.Matches,
            [.. referenceIds.Where(id => !matchedReferences.Contains(id))],
            [.. candidateIds.Where(id => !matchedCandidates.Contains(id))]
        );
    }
}
