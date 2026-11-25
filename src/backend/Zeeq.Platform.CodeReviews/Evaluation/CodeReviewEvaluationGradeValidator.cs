using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>A one-to-one judge assignment and its explanation.</summary>
public sealed record CodeReviewEvaluationMatch(
    string ReferenceId,
    string CandidateId,
    string Reason,
    CodeReviewEvaluationReferenceDisposition Disposition =
        CodeReviewEvaluationReferenceDisposition.Expected
);

/// <summary>Validated assignments. Unmatched candidates are unverified.</summary>
public sealed record CodeReviewEvaluationGrade(
    IReadOnlyList<CodeReviewEvaluationMatch> Matches,
    IReadOnlyList<string> MissedReferenceIds,
    IReadOnlyList<string> RepeatedRejectedReferenceIds,
    IReadOnlyList<string> AvoidedRejectedReferenceIds,
    IReadOnlyList<string> AdditionalCandidateIds,
    int CrossFileRejectedMatchesFiltered
);

/// <summary>A reference and its authoritative matching attributes.</summary>
public sealed record CodeReviewEvaluationReferenceTarget(
    string Id,
    CodeReviewEvaluationReferenceDisposition Disposition,
    string File
);

/// <summary>A candidate and its file.</summary>
public sealed record CodeReviewEvaluationCandidateTarget(string Id, string File);

/// <summary>Rejects malformed or inflated judge output before it is persisted.</summary>
public static class CodeReviewEvaluationGradeValidator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record JudgeResponse(List<CodeReviewEvaluationMatch>? Matches);

    /// <summary>Validates expected-only assignments.</summary>
    public static CodeReviewEvaluationGrade Validate(
        string json,
        IReadOnlyList<string> referenceIds,
        IReadOnlyList<string> candidateIds
    ) =>
        Validate(
            json,
            referenceIds
                .Select(id => new CodeReviewEvaluationReferenceTarget(
                    id,
                    CodeReviewEvaluationReferenceDisposition.Expected,
                    ""
                ))
                .ToArray(),
            candidateIds.Select(id => new CodeReviewEvaluationCandidateTarget(id, "")).ToArray()
        );

    /// <summary>Validates identifiers and derives the two grading axes.</summary>
    public static CodeReviewEvaluationGrade Validate(
        string json,
        IReadOnlyList<CodeReviewEvaluationReferenceTarget> references,
        IReadOnlyList<CodeReviewEvaluationCandidateTarget> candidates
    )
    {
        if (
            references.Any(x => string.IsNullOrWhiteSpace(x.Id))
            || candidates.Any(x => string.IsNullOrWhiteSpace(x.Id))
            || references.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count()
                != references.Count
            || candidates.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count()
                != candidates.Count
        )
            throw new InvalidOperationException("Finding identifiers must be unique and nonempty.");
        var refs = references.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var cands = candidates.ToDictionary(x => x.Id, StringComparer.Ordinal);
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
        // Validate raw assignments before filtering: even a dropped pair cannot share an ID.
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<CodeReviewEvaluationMatch>();
        var dropped = 0;
        foreach (var match in response.Matches)
        {
            if (
                match is null
                || string.IsNullOrWhiteSpace(match.ReferenceId)
                || string.IsNullOrWhiteSpace(match.CandidateId)
                || string.IsNullOrWhiteSpace(match.Reason)
                || !refs.TryGetValue(match.ReferenceId, out var reference)
                || !cands.TryGetValue(match.CandidateId, out var candidate)
                || !seenRefs.Add(match.ReferenceId)
                || !seenCandidates.Add(match.CandidateId)
            )
                throw new InvalidOperationException(
                    "Judge output contains an invalid or duplicate match."
                );
            if (
                reference.Disposition == CodeReviewEvaluationReferenceDisposition.Rejected
                && !string.Equals(
                    Normalize(reference.File),
                    Normalize(candidate.File),
                    StringComparison.Ordinal
                )
            )
            {
                dropped++;
                continue;
            }
            accepted.Add(match with { Disposition = reference.Disposition });
        }
        var matchedRefs = accepted.Select(x => x.ReferenceId).ToHashSet(StringComparer.Ordinal);
        var matchedCandidates = accepted
            .Select(x => x.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        return new(
            accepted,
            [
                .. references
                    .Where(x =>
                        x.Disposition == CodeReviewEvaluationReferenceDisposition.Expected
                        && !matchedRefs.Contains(x.Id)
                    )
                    .Select(x => x.Id),
            ],
            [
                .. references
                    .Where(x =>
                        x.Disposition == CodeReviewEvaluationReferenceDisposition.Rejected
                        && matchedRefs.Contains(x.Id)
                    )
                    .Select(x => x.Id),
            ],
            [
                .. references
                    .Where(x =>
                        x.Disposition == CodeReviewEvaluationReferenceDisposition.Rejected
                        && !matchedRefs.Contains(x.Id)
                    )
                    .Select(x => x.Id),
            ],
            [.. candidates.Where(x => !matchedCandidates.Contains(x.Id)).Select(x => x.Id)],
            dropped
        );
    }

    private static string Normalize(string file)
    {
        var path = file.Trim().Replace('\\', '/');
        while (path.StartsWith('/') || path.StartsWith("./", StringComparison.Ordinal))
            path = path.StartsWith('/') ? path[1..] : path[2..];
        return path;
    }
}
