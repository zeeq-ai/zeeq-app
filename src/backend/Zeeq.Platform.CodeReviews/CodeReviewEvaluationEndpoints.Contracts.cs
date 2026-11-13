using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Request to capture and add one pull request.</summary>
public sealed record AddCodeReviewEvaluationCaseRequest(
    int PullRequestNumber,
    long ExpectedVersion
);

/// <summary>Result of adding or finding an existing pull request membership.</summary>
public sealed record AddCodeReviewEvaluationCaseResponse(
    bool Added,
    string DatasetId,
    long DatasetVersion,
    int PullRequestNumber,
    string BaseSha,
    string HeadSha,
    DateTimeOffset CapturedAtUtc
);

/// <summary>
/// Evaluation dataset row returned to curation screens.
/// </summary>
/// <param name="Id">Stable dataset id.</param>
/// <param name="RepositoryId">Repository mapping the dataset belongs to.</param>
/// <param name="Name">Dataset label. Not an identity.</param>
/// <param name="Description">Optional operator description.</param>
/// <param name="Version">Current content version used for optimistic concurrency.</param>
/// <param name="CaseCount">Number of live cases.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="UpdatedAtUtc">Last save timestamp.</param>
/// <param name="ArchivedAtUtc">Archive timestamp, when archived.</param>
public sealed record CodeReviewEvaluationDatasetSummaryDto(
    string Id,
    string RepositoryId,
    string Name,
    string? Description,
    long Version,
    int CaseCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc
);

/// <summary>Dataset list response.</summary>
/// <param name="Items">Datasets, most recently updated first.</param>
public sealed record CodeReviewEvaluationDatasetListResponse(
    IReadOnlyList<CodeReviewEvaluationDatasetSummaryDto> Items
);

/// <summary>
/// Full dataset draft returned to curation.
/// </summary>
/// <param name="Id">Stable dataset id.</param>
/// <param name="RepositoryId">Repository mapping the dataset belongs to.</param>
/// <param name="Name">Dataset label.</param>
/// <param name="Description">Optional operator description.</param>
/// <param name="Version">Current content version.</param>
/// <param name="CaseCount">Number of live cases.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="UpdatedAtUtc">Last save timestamp.</param>
/// <param name="ArchivedAtUtc">Archive timestamp, when archived.</param>
/// <param name="Cases">Live cases in pull request number order.</param>
public sealed record CodeReviewEvaluationDatasetDetailDto(
    string Id,
    string RepositoryId,
    string Name,
    string? Description,
    long Version,
    int CaseCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    IReadOnlyList<CodeReviewEvaluationCaseDto> Cases
);

/// <summary>Single dataset response.</summary>
/// <param name="Dataset">Dataset draft.</param>
public sealed record CodeReviewEvaluationDatasetResponse(
    CodeReviewEvaluationDatasetDetailDto Dataset
);

/// <summary>One captured pull request case.</summary>
/// <param name="Id">Stable case id. Survives remove and re-add.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Captured pull request title. Not a live provider read.</param>
/// <param name="PullRequestRecordId">Known local pull request record id, when the PR existed in Zeeq.</param>
/// <param name="BaseSha">Captured base commit identity.</param>
/// <param name="HeadSha">Captured head commit identity.</param>
/// <param name="SnapshotSha256">SHA-256 of the captured snapshot artifact.</param>
/// <param name="CapturedAtUtc">Capture timestamp.</param>
/// <param name="ChangedFileCount">Changed files visible to the reviewer.</param>
/// <param name="OmittedFileCount">Changed files omitted from the snapshot, for example binary files.</param>
/// <param name="RemovedAtUtc">Soft-removal timestamp, when the case is no longer a member.</param>
/// <param name="ReferenceSets">Reference expectations keyed by reviewer agent id.</param>
public sealed record CodeReviewEvaluationCaseDto(
    string Id,
    int PullRequestNumber,
    string Title,
    string? PullRequestRecordId,
    string BaseSha,
    string HeadSha,
    string SnapshotSha256,
    DateTimeOffset CapturedAtUtc,
    int ChangedFileCount,
    int OmittedFileCount,
    DateTimeOffset? RemovedAtUtc,
    IReadOnlyList<CodeReviewEvaluationReferenceSetDto> ReferenceSets
);

/// <summary>One reviewer's reference expectations for one case.</summary>
/// <param name="ReviewerAgentId">Stable reviewer agent id.</param>
/// <param name="State">Unlabelled, Expected or ReviewedClean.</param>
/// <param name="Findings">Expected findings. Empty is valid for ReviewedClean.</param>
public sealed record CodeReviewEvaluationReferenceSetDto(
    string ReviewerAgentId,
    CodeReviewEvaluationReferenceState State,
    IReadOnlyList<CodeReviewEvaluationReferenceFindingDto> Findings
);

/// <summary>One editable reference finding.</summary>
/// <param name="Id">Stable finding id used by the judge.</param>
/// <param name="Title">Short expectation title, at most 300 characters.</param>
/// <param name="Summary">What the reviewer should report, at most 4000 characters.</param>
/// <param name="Severity">Expected severity.</param>
/// <param name="File">Repository-relative file path.</param>
/// <param name="Line">Optional expected line.</param>
/// <param name="Origin">How the finding entered the dataset.</param>
/// <param name="SourceRunId">Evaluation run it was adopted from, when applicable.</param>
/// <param name="SourceFindingId">Candidate finding id inside the source run, when applicable.</param>
public sealed record CodeReviewEvaluationReferenceFindingDto(
    string Id,
    [property: Required, MaxLength(300)] string Title,
    [property: Required, MaxLength(4000)] string Summary,
    CodeReviewEvaluationFindingSeverity Severity,
    [property: Required] string File,
    int? Line,
    CodeReviewEvaluationReferenceOrigin Origin,
    string? SourceRunId,
    string? SourceFindingId
);

/// <summary>Creates one named dataset. Idempotent on the request key.</summary>
/// <param name="Name">Dataset label, at most 256 characters.</param>
/// <param name="Description">Optional description, at most 2000 characters.</param>
/// <param name="CreateRequestKey">Client idempotency key. Retries return the same dataset.</param>
public sealed record CreateCodeReviewEvaluationDatasetRequest(
    [property: Required, MaxLength(256)] string Name,
    [property: MaxLength(2000)] string? Description,
    [property: Required, MaxLength(128)] string CreateRequestKey
);

/// <summary>Saves one complete dataset draft under an expected version.</summary>
/// <param name="ExpectedVersion">Version the caller loaded.</param>
/// <param name="Name">Desired dataset name.</param>
/// <param name="Description">Desired description.</param>
/// <param name="Cases">Complete desired case membership.</param>
public sealed record SaveCodeReviewEvaluationDatasetRequest(
    [property: Range(1, long.MaxValue)] long ExpectedVersion,
    [property: Required, MaxLength(256)] string Name,
    [property: MaxLength(2000)] string? Description,
    [property: Required] IReadOnlyList<CodeReviewEvaluationDatasetCaseChangeDto> Cases
);

/// <summary>One case inside a dataset save draft.</summary>
/// <param name="CaseId">Existing case id, or null for new or re-added membership.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="CaptureToken">Staged capture token. Required when <paramref name="CaseId"/> is null.</param>
/// <param name="ReferenceSets">Replacement reference sets, or null to leave existing content unchanged.</param>
public sealed record CodeReviewEvaluationDatasetCaseChangeDto(
    string? CaseId,
    [property: Range(1, 999_999)] int PullRequestNumber,
    string? CaptureToken,
    IReadOnlyList<CodeReviewEvaluationReferenceSetDto>? ReferenceSets
);

/// <summary>One known pull request offered as an evaluation target.</summary>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Current provider title.</param>
/// <param name="State">Current provider state.</param>
/// <param name="HeadSha">Current head commit identity.</param>
/// <param name="AuthorLogin">Provider login of the author.</param>
/// <param name="UpdatedAtUtc">Most recent update timestamp.</param>
/// <param name="AlreadyInDataset">True when the dataset already contains this pull request.</param>
public sealed record CodeReviewEvaluationPrTargetDto(
    int PullRequestNumber,
    string Title,
    PullRequestState State,
    string HeadSha,
    string AuthorLogin,
    DateTimeOffset UpdatedAtUtc,
    bool AlreadyInDataset
);

/// <summary>Page of known evaluation targets.</summary>
/// <param name="Items">Matching rows, newest first.</param>
/// <param name="NextCursor">Opaque cursor for the next page, when one exists.</param>
public sealed record CodeReviewEvaluationPrTargetListResponse(
    IReadOnlyList<CodeReviewEvaluationPrTargetDto> Items,
    string? NextCursor
);

/// <summary>Resolves and stages one or more manual pull request entries.</summary>
/// <param name="DatasetId">Persisted dataset the captures are staged for.</param>
/// <param name="ExpectedDatasetVersion">Dataset version the captures are staged against.</param>
/// <param name="Entries">Numbers, <c>#</c> numbers, comma or newline lists, or same-repository PR URLs.</param>
public sealed record ResolveCodeReviewEvaluationPrTargetsRequest(
    [property: Required] string DatasetId,
    [property: Range(1, long.MaxValue)] long ExpectedDatasetVersion,
    [property: Required] IReadOnlyList<string> Entries
);

/// <summary>Per-entry resolution results. A bad entry never clears valid choices.</summary>
/// <param name="Items">One result per entry, in request order.</param>
public sealed record ResolveCodeReviewEvaluationPrTargetsResponse(
    IReadOnlyList<CodeReviewEvaluationPrResolutionDto> Items
);

/// <summary>One entry resolution outcome.</summary>
/// <param name="Entry">Original entry text.</param>
/// <param name="PullRequestNumber">Resolved provider pull request number.</param>
/// <param name="Target">Known pull request row, when it already exists in Zeeq.</param>
/// <param name="CaptureToken">Opaque staged capture token for Save.</param>
/// <param name="ExpiresAtUtc">Token expiry.</param>
/// <param name="Preview">Capture preview for confirmation.</param>
/// <param name="ErrorCode">Stable per-entry error code.</param>
/// <param name="ErrorMessage">Human-readable per-entry error.</param>
public sealed record CodeReviewEvaluationPrResolutionDto(
    string Entry,
    int? PullRequestNumber,
    CodeReviewEvaluationPrTargetDto? Target,
    string? CaptureToken,
    DateTimeOffset? ExpiresAtUtc,
    CodeReviewEvaluationCapturePreviewDto? Preview,
    string? ErrorCode,
    string? ErrorMessage
);

/// <summary>Summary of a staged capture, shown before the draft is saved.</summary>
/// <remarks>
/// Carries everything a staged row needs, so a pull request that does not exist in Zeeq yet
/// can still be rendered and added to the draft without a separate lookup.
/// </remarks>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Pull request title captured with the snapshot.</param>
/// <param name="State">Provider state at capture time.</param>
/// <param name="AuthorLogin">Provider login of the pull request author.</param>
/// <param name="BaseSha">Captured base commit identity.</param>
/// <param name="HeadSha">Captured head commit identity.</param>
/// <param name="ChangedFileCount">Changed files visible to the reviewer.</param>
/// <param name="OmittedFileCount">Changed files omitted from the snapshot.</param>
/// <param name="CapturedAtUtc">Capture timestamp.</param>
public sealed record CodeReviewEvaluationCapturePreviewDto(
    int PullRequestNumber,
    string Title,
    PullRequestState State,
    string AuthorLogin,
    string BaseSha,
    string HeadSha,
    int ChangedFileCount,
    int OmittedFileCount,
    DateTimeOffset CapturedAtUtc
);

/// <summary>Saved capture for one dataset case.</summary>
/// <param name="Snapshot">Immutable snapshot envelope captured for the case.</param>
public sealed record CodeReviewEvaluationCaseSnapshotResponse(
    CodeReviewEvaluationSnapshot Snapshot
);

/// <summary>Staged refresh result for one existing case.</summary>
/// <param name="CaptureToken">Opaque staged capture token for the next Save.</param>
/// <param name="ExpiresAtUtc">Token expiry.</param>
/// <param name="Preview">Capture preview for the confirmation step.</param>
public sealed record RefreshCodeReviewEvaluationCaseResponse(
    string CaptureToken,
    DateTimeOffset ExpiresAtUtc,
    CodeReviewEvaluationCapturePreviewDto Preview
);

/// <summary>Revision row in the reviewer history list.</summary>
/// <param name="Id">Immutable generated revision id.</param>
/// <param name="AgentId">Reviewer agent the revision belongs to.</param>
/// <param name="Label">Optional operator label. Empty when unset.</param>
/// <param name="ContentHash">Content hash used for deduplication.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="CreatedByUserId">User that produced the revision.</param>
public sealed record CodeReviewerAgentRevisionSummaryDto(
    string Id,
    string AgentId,
    string Label,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    string CreatedByUserId
);

/// <summary>Revision list response.</summary>
/// <param name="Items">Revisions, newest first.</param>
public sealed record CodeReviewerAgentRevisionListResponse(
    IReadOnlyList<CodeReviewerAgentRevisionSummaryDto> Items
);

/// <summary>Full revision content used by the read-only diff view.</summary>
/// <param name="Id">Immutable generated revision id.</param>
/// <param name="AgentId">Reviewer agent the revision belongs to.</param>
/// <param name="Label">Optional operator label. Empty when unset.</param>
/// <param name="ContentHash">Content hash used for deduplication.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="CreatedByUserId">User that produced the revision.</param>
/// <param name="DisplayName">Reviewer name at capture time.</param>
/// <param name="ReviewFacet">Review facet at capture time.</param>
/// <param name="ModelTier">Semantic model tier at capture time.</param>
/// <param name="Prompt">Reviewer instructions at capture time.</param>
/// <param name="Enabled">Whether the agent was enabled at capture time.</param>
/// <param name="ActivationConfiguration">File activation rules at capture time.</param>
public sealed record CodeReviewerAgentRevisionDto(
    string Id,
    string AgentId,
    string Label,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    string CreatedByUserId,
    string DisplayName,
    string ReviewFacet,
    CodeReviewModelTier ModelTier,
    string Prompt,
    bool Enabled,
    CodeReviewerActivationConfigurationDto ActivationConfiguration
);

/// <summary>Single revision response.</summary>
/// <param name="Revision">Full revision content.</param>
public sealed record CodeReviewerAgentRevisionResponse(CodeReviewerAgentRevisionDto Revision);
