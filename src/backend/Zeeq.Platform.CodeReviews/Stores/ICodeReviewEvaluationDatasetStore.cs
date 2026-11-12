using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for versioned evaluation datasets and their pull request cases.
/// </summary>
/// <remarks>
/// Saves are compare-and-swap on <see cref="CodeReviewEvaluationDataset.Version"/> under a
/// dataset row lock, so concurrent saves produce exactly one winner and never a partially
/// applied membership set. Run admission reads the same row under the same lock, so a run
/// manifest always reflects one internally consistent dataset version.
/// </remarks>
public interface ICodeReviewEvaluationDatasetStore
{
    /// <summary>
    /// Creates a named dataset idempotently.
    /// </summary>
    /// <remarks>
    /// A repeated create with the same organization and <c>CreateRequestKey</c> returns the
    /// existing row instead of creating a duplicate.
    /// </remarks>
    Task<CodeReviewEvaluationDataset> CreateAsync(
        CodeReviewEvaluationDataset dataset,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one dataset, optionally including archived rows.</summary>
    Task<CodeReviewEvaluationDataset?> FindAsync(
        string organizationId,
        string datasetId,
        bool includeArchived,
        CancellationToken cancellationToken
    );

    /// <summary>Lists datasets for one repository, newest first.</summary>
    Task<IReadOnlyList<CodeReviewEvaluationDatasetSummary>> ListAsync(
        CodeReviewEvaluationDatasetQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>Lists cases for one dataset, optionally including soft-removed members.</summary>
    Task<IReadOnlyList<CodeReviewEvaluationCase>> ListCasesAsync(
        string organizationId,
        string datasetId,
        bool includeRemoved,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one case inside a dataset, optionally including soft-removed members.</summary>
    Task<CodeReviewEvaluationCase?> FindCaseAsync(
        string organizationId,
        string datasetId,
        string caseId,
        bool includeRemoved,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Applies one complete dataset draft under the dataset row lock.
    /// </summary>
    /// <remarks>
    /// Membership is the complete desired set: listed existing case ids are kept, listed
    /// null case ids are new captures, and existing cases missing from the request are
    /// soft-removed. Re-adding a removed pull request reuses its case identity. A fresh
    /// capture resets that case's reference sets to unlabelled. The version increments
    /// exactly once on success.
    /// </remarks>
    Task<CodeReviewEvaluationDatasetSaveResult> SaveAsync(
        CodeReviewEvaluationDatasetSaveRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>Archives a dataset. Run history and artifacts are never cascaded.</summary>
    Task<CodeReviewEvaluationDataset?> ArchiveAsync(
        string organizationId,
        string datasetId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Filter for listing evaluation datasets.
/// </summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RepositoryId">Repository mapping scope.</param>
/// <param name="IncludeArchived">Whether archived datasets are returned.</param>
public sealed record CodeReviewEvaluationDatasetQuery(
    string OrganizationId,
    string RepositoryId,
    bool IncludeArchived = false
);

/// <summary>
/// Dataset row plus its live case count for list views.
/// </summary>
/// <param name="Dataset">Persisted dataset row.</param>
/// <param name="CaseCount">Number of non-removed cases in the dataset.</param>
public sealed record CodeReviewEvaluationDatasetSummary(
    CodeReviewEvaluationDataset Dataset,
    int CaseCount
);

/// <summary>
/// One dataset draft applied by <see cref="ICodeReviewEvaluationDatasetStore.SaveAsync"/>.
/// </summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RepositoryId">Repository mapping scope.</param>
/// <param name="DatasetId">Dataset being saved.</param>
/// <param name="ExpectedVersion">Version the caller loaded. Must match the locked row.</param>
/// <param name="Name">Desired dataset name.</param>
/// <param name="Description">Desired dataset description.</param>
/// <param name="Cases">Complete desired case membership.</param>
public sealed record CodeReviewEvaluationDatasetSaveRequest(
    string OrganizationId,
    string RepositoryId,
    string DatasetId,
    long ExpectedVersion,
    string Name,
    string? Description,
    IReadOnlyList<CodeReviewEvaluationCaseSave> Cases
);

/// <summary>
/// One case inside a dataset save request.
/// </summary>
/// <param name="CaseId">Existing case id, or null for a new or re-added pull request.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Capture">Resolved capture to install. Required for new cases and for refresh.</param>
/// <param name="ReferenceSets">Replacement reference sets, or null to keep existing content.</param>
public sealed record CodeReviewEvaluationCaseSave(
    string? CaseId,
    int PullRequestNumber,
    CodeReviewEvaluationStagedCapture? Capture,
    IReadOnlyList<CodeReviewEvaluationReferenceSet>? ReferenceSets
);

/// <summary>
/// Resolved capture staged by the capture service before the save transaction starts.
/// </summary>
/// <remarks>
/// The snapshot artifact bytes are already written when this record exists, so the save
/// transaction never performs provider or storage I/O.
/// </remarks>
/// <param name="Snapshot">Immutable snapshot artifact reference.</param>
/// <param name="PullRequestNumber">Provider pull request number the capture belongs to.</param>
/// <param name="PullRequestTitle">Pull request title captured with the snapshot.</param>
/// <param name="AuthorLogin">Provider login of the pull request author at capture time.</param>
/// <param name="BaseSha">Captured base commit identity.</param>
/// <param name="HeadSha">Captured head commit identity.</param>
/// <param name="State">Provider state at capture time.</param>
/// <param name="CapturedAtUtc">Capture time.</param>
/// <param name="ChangedFileCount">Changed files visible to the reviewer.</param>
/// <param name="OmittedFileCount">Changed files omitted from the snapshot.</param>
/// <param name="DeclaredChangedFileCount">Changed-file count declared by the provider.</param>
/// <param name="PullRequestRecordId">Local pull request record id when the pull request is already known.</param>
public sealed record CodeReviewEvaluationStagedCapture(
    CodeReviewEvaluationArtifact Snapshot,
    int PullRequestNumber,
    string PullRequestTitle,
    string AuthorLogin,
    string BaseSha,
    string HeadSha,
    PullRequestState State,
    DateTimeOffset CapturedAtUtc,
    int ChangedFileCount,
    int OmittedFileCount,
    int DeclaredChangedFileCount,
    string? PullRequestRecordId
);

/// <summary>
/// Save outcome without exceptions, so handlers can map conflicts to HTTP directly.
/// </summary>
public enum CodeReviewEvaluationDatasetSaveOutcome
{
    /// <summary>The draft was applied and the version advanced once.</summary>
    Saved,

    /// <summary>The dataset does not exist in the authorized organization/repository.</summary>
    DatasetNotFound,

    /// <summary>The caller's expected version is stale. Nothing was written.</summary>
    VersionConflict,

    /// <summary>The draft is internally invalid, for example a missing capture or duplicate PR number.</summary>
    InvalidCase,
}

/// <summary>
/// Result of one dataset save attempt.
/// </summary>
/// <param name="Outcome">Save outcome.</param>
/// <param name="Dataset">Dataset after a successful save.</param>
/// <param name="Cases">Live cases after a successful save.</param>
public sealed record CodeReviewEvaluationDatasetSaveResult(
    CodeReviewEvaluationDatasetSaveOutcome Outcome,
    CodeReviewEvaluationDataset? Dataset,
    IReadOnlyList<CodeReviewEvaluationCase>? Cases
);
