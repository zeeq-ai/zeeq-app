using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Maps evaluation domain rows to API contracts and back.
/// </summary>
/// <remarks>
/// DTOs never expose storage URIs, byte counts, lease tokens, key references or manifest
/// content. Reference findings lose their approval audit fields on the way in; the handler
/// stamps the acting user and time so a client cannot forge them.
/// </remarks>
internal static class CodeReviewEvaluationEndpointMapping
{
    /// <summary>Maps one dataset with its live cases.</summary>
    public static CodeReviewEvaluationDatasetDetailDto ToDetailDto(
        CodeReviewEvaluationDataset dataset,
        IReadOnlyList<CodeReviewEvaluationCase> cases
    ) =>
        new(
            dataset.Id,
            dataset.RepositoryId,
            dataset.Name,
            dataset.Description,
            dataset.Version,
            cases.Count(item => item.RemovedAtUtc is null),
            dataset.CreatedAtUtc,
            dataset.UpdatedAtUtc,
            dataset.ArchivedAtUtc,
            [.. cases.Where(item => item.RemovedAtUtc is null).Select(ToDto)]
        );

    /// <summary>Maps one dataset summary.</summary>
    public static CodeReviewEvaluationDatasetSummaryDto ToSummaryDto(
        CodeReviewEvaluationDatasetSummary summary
    ) =>
        new(
            summary.Dataset.Id,
            summary.Dataset.RepositoryId,
            summary.Dataset.Name,
            summary.Dataset.Description,
            summary.Dataset.Version,
            summary.CaseCount,
            summary.Dataset.CreatedAtUtc,
            summary.Dataset.UpdatedAtUtc,
            summary.Dataset.ArchivedAtUtc
        );

    /// <summary>Maps one captured case.</summary>
    public static CodeReviewEvaluationCaseDto ToDto(CodeReviewEvaluationCase item) =>
        new(
            item.Id,
            item.PullRequestNumber,
            item.PullRequestTitle,
            item.PullRequestRecordId,
            item.BaseSha,
            item.HeadSha,
            item.SnapshotSha256,
            item.CapturedAtUtc,
            item.ChangedFileCount,
            item.OmittedFileCount,
            item.RemovedAtUtc,
            [.. item.ReferenceSets.Select(ToDto)]
        );

    /// <summary>Maps one reference set.</summary>
    public static CodeReviewEvaluationReferenceSetDto ToDto(CodeReviewEvaluationReferenceSet set) =>
        new(set.ReviewerAgentId, set.State, [.. set.Findings.Select(ToDto)]);

    /// <summary>Maps one reference finding.</summary>
    public static CodeReviewEvaluationReferenceFindingDto ToDto(
        CodeReviewEvaluationReferenceFinding finding
    ) =>
        new(
            finding.Id,
            finding.Title,
            finding.Summary,
            finding.Severity,
            finding.File,
            finding.Line,
            finding.Origin,
            finding.SourceRunId,
            finding.SourceFindingId
        );

    /// <summary>Maps a submitted reference set, stamping the acting user and time.</summary>
    public static CodeReviewEvaluationReferenceSet ToModel(
        CodeReviewEvaluationReferenceSetDto dto,
        string approvedByUserId,
        DateTimeOffset approvedAtUtc
    ) =>
        new(
            dto.ReviewerAgentId,
            dto.State,
            [
                .. dto.Findings.Select(finding => new CodeReviewEvaluationReferenceFinding(
                    finding.Id,
                    finding.Title.Trim(),
                    finding.Summary.Trim(),
                    finding.Severity,
                    finding.File.Trim(),
                    finding.Line,
                    finding.Origin,
                    finding.SourceRunId,
                    finding.SourceFindingId,
                    approvedByUserId,
                    approvedAtUtc
                )),
            ]
        );

    /// <summary>Maps one known pull request target.</summary>
    public static CodeReviewEvaluationPrTargetDto ToDto(
        CodeReviewEvaluationPrTargetRecord record,
        IReadOnlySet<int> memberNumbers
    ) =>
        new(
            record.PullRequestNumber,
            record.Title,
            record.State,
            record.HeadSha,
            record.AuthorLogin,
            record.UpdatedAtUtc,
            memberNumbers.Contains(record.PullRequestNumber)
        );

    /// <summary>Maps one staged capture preview.</summary>
    public static CodeReviewEvaluationCapturePreviewDto ToPreview(
        CodeReviewEvaluationStagedCapture capture,
        int pullRequestNumber
    ) =>
        new(
            pullRequestNumber,
            capture.PullRequestTitle,
            capture.State,
            capture.AuthorLogin,
            capture.BaseSha,
            capture.HeadSha,
            capture.ChangedFileCount,
            capture.OmittedFileCount,
            capture.CapturedAtUtc
        );

    /// <summary>Maps one revision summary.</summary>
    public static CodeReviewerAgentRevisionSummaryDto ToSummaryDto(
        CodeReviewerAgentRevision revision
    ) =>
        new(
            revision.Id,
            revision.AgentId,
            revision.Label,
            revision.ContentHash,
            revision.CreatedAtUtc,
            revision.CreatedByUserId
        );

    /// <summary>Maps one full revision.</summary>
    public static CodeReviewerAgentRevisionDto ToDto(CodeReviewerAgentRevision revision) =>
        new(
            revision.Id,
            revision.AgentId,
            revision.Label,
            revision.ContentHash,
            revision.CreatedAtUtc,
            revision.CreatedByUserId,
            revision.Configuration.DisplayName,
            revision.Configuration.ReviewFacet,
            revision.Configuration.ModelTier,
            revision.Configuration.Prompt,
            revision.Configuration.Enabled,
            CodeReviewEndpointMapping.ToDto(revision.Configuration.ActivationConfiguration)
        );
}
