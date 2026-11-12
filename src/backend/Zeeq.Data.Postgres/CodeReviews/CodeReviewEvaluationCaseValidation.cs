using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Store-level shape guards for evaluation draft saves.
/// </summary>
/// <remarks>
/// Handlers perform the user-facing validation and return actionable error codes. This guard
/// exists so a direct store caller cannot persist a draft that violates the v1 limits or
/// leaves a new case without a capture.
/// </remarks>
internal static class CodeReviewEvaluationCaseValidation
{
    /// <summary>Returns a failure reason, or null when the draft shape is acceptable.</summary>
    public static string? ValidateSaveRequest(CodeReviewEvaluationDatasetSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 256)
        {
            return "dataset_name_invalid";
        }

        if (
            request.Description is
            { Length: > CodeReviewEvaluationLimits.MaxDatasetDescriptionLength }
        )
        {
            return "dataset_description_too_long";
        }

        if (request.Cases.Count > PostgresCodeReviewEvaluationDatasetStore.MaxCasesPerDataset)
        {
            return "case_limit_reached";
        }

        if (
            request.Cases.Select(item => item.PullRequestNumber).Distinct().Count()
            != request.Cases.Count
        )
        {
            return "duplicate_pull_request";
        }

        foreach (var item in request.Cases)
        {
            if (item.PullRequestNumber <= 0)
            {
                return "invalid_pull_request_number";
            }

            if (item.CaseId is null && item.Capture is null)
            {
                return "capture_required";
            }

            // Defense in depth: a capture must describe the same pull request it is installed
            // on, so one PR's snapshot can never label another PR's case.
            if (
                item.Capture is not null
                && item.Capture.PullRequestNumber != item.PullRequestNumber
            )
            {
                return "capture_pull_request_mismatch";
            }

            if (item.ReferenceSets is not null)
            {
                var referenceError = ValidateReferenceSets(item.ReferenceSets);
                if (referenceError is not null)
                {
                    return referenceError;
                }
            }
        }

        return null;
    }

    /// <summary>Returns a failure reason when one reviewer's reference set breaks the v1 limits.</summary>
    public static string? ValidateReferenceSets(
        IReadOnlyList<Zeeq.Core.Models.CodeReviewEvaluationReferenceSet> referenceSets
    )
    {
        if (referenceSets.Count == 0)
        {
            return null;
        }

        var reviewerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var set in referenceSets)
        {
            if (string.IsNullOrWhiteSpace(set.ReviewerAgentId))
            {
                return "reference_reviewer_required";
            }

            if (!Enum.IsDefined(set.State))
            {
                return "reference_state_invalid";
            }

            // Only an Expected set can carry expectations. The UI may keep fields in an open
            // editor until Save, but it projects non-expected states to no findings; a direct
            // store call that persists ReviewedClean/Unlabelled with findings is contradictory
            // and would corrupt coverage accounting.
            if (
                set.State != Zeeq.Core.Models.CodeReviewEvaluationReferenceState.Expected
                && set.Findings.Count > 0
            )
            {
                return "reference_state_conflict";
            }

            if (!reviewerIds.Add(set.ReviewerAgentId))
            {
                return "reference_reviewer_duplicate";
            }

            if (set.Findings.Count > CodeReviewEvaluationLimits.MaxReferenceFindings)
            {
                return "reference_limit_reached";
            }

            var findingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in set.Findings)
            {
                if (string.IsNullOrWhiteSpace(finding.Id) || !findingIds.Add(finding.Id))
                {
                    return "reference_finding_id_invalid";
                }

                if (
                    string.IsNullOrWhiteSpace(finding.Title)
                    || finding.Title.Length > CodeReviewEvaluationLimits.MaxReferenceTitleLength
                )
                {
                    return "reference_title_invalid";
                }

                if (
                    string.IsNullOrWhiteSpace(finding.Summary)
                    || finding.Summary.Length > CodeReviewEvaluationLimits.MaxReferenceSummaryLength
                )
                {
                    return "reference_summary_invalid";
                }

                if (string.IsNullOrWhiteSpace(finding.File))
                {
                    return "reference_file_required";
                }
            }
        }

        return null;
    }
}
