using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Promotes one confirmed PR false positive into a versioned dataset.</summary>
public sealed class PromoteFindingTriageHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    ICodeReviewFindingTriageStore triages
) : IEndpointHandler
{
    /// <summary>Validates scope and applies the promotion through the dataset CAS save.</summary>
    public async Task<Results<NotFound, ForbidHttpResult, BadRequest<CodeReviewEndpointError>, Conflict<CodeReviewEndpointError>, Ok<CodeReviewEvaluationDatasetResponse>>> HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        string triageId,
        PromoteFindingTriageRequest? request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (access is null) return TypedResults.NotFound();
        if (!access.CanManage) return TypedResults.Forbid();
        if (request is null || string.IsNullOrWhiteSpace(request.ReviewerAgentId) || request.ExpectedVersion <= 0)
        {
            return TypedResults.BadRequest(new CodeReviewEndpointError("invalid_request", "A reviewer and positive dataset version are required."));
        }

        var repository = await repositories.FindActiveForOrganizationAsync(organizationId, repositoryId, cancellationToken);
        var dataset = await datasets.FindAsync(organizationId, datasetId, includeArchived: false, cancellationToken);
        var triage = await triages.FindAsync(organizationId, triageId, cancellationToken);
        if (repository is null || dataset is null || dataset.RepositoryId != repository.Id || triage is null || triage.RepositoryId != repository.Id || triage.PullRequestNumber is null || triage.ApprovalState != CodeReviewFindingApprovalState.Confirmed || triage.Channel == CodeReviewFindingTriageChannel.AgentAttested || triage.Disposition != CodeReviewFindingDisposition.FalsePositive)
        {
            return TypedResults.NotFound();
        }
        if (dataset.Version != request.ExpectedVersion)
        {
            return TypedResults.Conflict(new CodeReviewEndpointError("dataset_version_conflict", "Reload the dataset and retry the promotion."));
        }

        var cases = await datasets.ListCasesAsync(organizationId, datasetId, includeRemoved: false, cancellationToken);
        var target = cases.FirstOrDefault(item => item.PullRequestNumber == triage.PullRequestNumber.Value);
        if (target is null) return TypedResults.BadRequest(new CodeReviewEndpointError("pull_request_not_in_dataset", "Add this pull request to the dataset before promoting a finding."));
        var existingSet = target.ReferenceSets.FirstOrDefault(set => set.ReviewerAgentId == request.ReviewerAgentId);
        var findingId = $"historical-{triage.Id}";
        if (existingSet?.Findings.Any(item => item.SourceFindingId == $"{triage.SourceReviewId}#{triage.OccurrenceKey}") == true)
        {
            return TypedResults.BadRequest(new CodeReviewEndpointError("already_promoted", "This finding is already in the reviewer reference set."));
        }

        var historicalFinding = new CodeReviewEvaluationReferenceFinding(
            findingId,
            triage.SemanticId,
            triage.FindingSummary,
            triage.Severity switch
            {
                CodeReviewFindingLevel.Critical => CodeReviewEvaluationFindingSeverity.Critical,
                CodeReviewFindingLevel.Major => CodeReviewEvaluationFindingSeverity.Major,
                CodeReviewFindingLevel.Minor => CodeReviewEvaluationFindingSeverity.Minor,
                CodeReviewFindingLevel.Suggestion => CodeReviewEvaluationFindingSeverity.Suggestion,
                CodeReviewFindingLevel.Comment => CodeReviewEvaluationFindingSeverity.Comment,
                _ => throw new ArgumentOutOfRangeException(nameof(triage.Severity)),
            },
            string.IsNullOrWhiteSpace(triage.FilePath) ? "(unknown file)" : triage.FilePath,
            triage.LineNumber,
            CodeReviewEvaluationReferenceOrigin.HistoricalReview,
            null,
            $"{triage.SourceReviewId}#{triage.OccurrenceKey}",
            access.UserId,
            DateTimeOffset.UtcNow,
            CodeReviewEvaluationReferenceDisposition.Rejected,
            triage.CurrentReason
        );
        var referenceSets = target.ReferenceSets.Where(set => set.ReviewerAgentId != request.ReviewerAgentId).ToList();
        referenceSets.Add(new CodeReviewEvaluationReferenceSet(
            request.ReviewerAgentId,
            existingSet?.State ?? CodeReviewEvaluationReferenceState.Unlabelled,
            [.. (existingSet?.Findings ?? []).Append(historicalFinding)]
        ));
        var save = await datasets.SaveAsync(new CodeReviewEvaluationDatasetSaveRequest(
            organizationId,
            repository.Id,
            dataset.Id,
            request.ExpectedVersion,
            dataset.Name,
            dataset.Description,
            [.. cases.Select(item => new CodeReviewEvaluationCaseSave(
                item.Id,
                item.PullRequestNumber,
                null,
                item.Id == target.Id ? referenceSets : item.ReferenceSets
            ))]
        ), cancellationToken);

        return save.Outcome switch
        {
            CodeReviewEvaluationDatasetSaveOutcome.Saved => TypedResults.Ok(new CodeReviewEvaluationDatasetResponse(CodeReviewEvaluationEndpointMapping.ToDetailDto(save.Dataset!, save.Cases!))),
            CodeReviewEvaluationDatasetSaveOutcome.VersionConflict => TypedResults.Conflict(new CodeReviewEndpointError("dataset_version_conflict", "Reload the dataset and retry the promotion.")),
            _ => TypedResults.BadRequest(new CodeReviewEndpointError("promotion_invalid", "The finding cannot be promoted to this dataset.")),
        };
    }
}
