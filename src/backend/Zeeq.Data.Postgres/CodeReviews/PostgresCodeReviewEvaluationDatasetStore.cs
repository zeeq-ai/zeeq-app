using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for evaluation datasets and their case membership.
/// </summary>
/// <remarks>
/// Saves take the dataset advisory lock and a row lock, verify the caller's expected version,
/// install the staged membership set and increment the version exactly once. Provider and
/// artifact I/O always happen before the save transaction, so no remote call runs inside it.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationDatasetStore(
    PostgresDbContext db,
    TimeProvider timeProvider
) : ICodeReviewEvaluationDatasetStore
{
    /// <summary>Maximum live cases allowed per dataset.</summary>
    internal const int MaxCasesPerDataset = CodeReviewEvaluationLimits.MaxCases;

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationDataset> CreateAsync(
        CodeReviewEvaluationDataset dataset,
        CancellationToken cancellationToken
    )
    {
        var existing = await db
            .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                "code_review_evaluation_dataset.find_by_request_key"
            )
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == dataset.OrganizationId
                    && candidate.CreateRequestKey == dataset.CreateRequestKey,
                cancellationToken
            );

        if (existing is not null)
        {
            return existing;
        }

        db.CodeReviewEvaluationDatasets.Add(dataset);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return dataset;
        }
        catch (DbUpdateException)
        {
            // A concurrent retry won the unique request-key index. Return that row so the
            // create call stays idempotent instead of creating a second dataset.
            db.Entry(dataset).State = EntityState.Detached;

            var winner = await db
                .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                    "code_review_evaluation_dataset.find_by_request_key"
                )
                .FirstOrDefaultAsync(
                    candidate =>
                        candidate.OrganizationId == dataset.OrganizationId
                        && candidate.CreateRequestKey == dataset.CreateRequestKey,
                    cancellationToken
                );

            return winner
                ?? throw new InvalidOperationException(
                    "A dataset create race could not be resolved for its request key."
                );
        }
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationDataset?> FindAsync(
        string organizationId,
        string datasetId,
        bool includeArchived,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                "code_review_evaluation_dataset.find"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                dataset =>
                    dataset.OrganizationId == organizationId
                    && dataset.Id == datasetId
                    && dataset.DisabledAtUtc == null
                    && (includeArchived || dataset.ArchivedAtUtc == null),
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewEvaluationDatasetSummary>> ListAsync(
        CodeReviewEvaluationDatasetQuery query,
        CancellationToken cancellationToken
    )
    {
        var datasets = await db
            .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                "code_review_evaluation_dataset.list"
            )
            .AsNoTracking()
            .Where(dataset =>
                dataset.OrganizationId == query.OrganizationId
                && dataset.RepositoryId == query.RepositoryId
                && dataset.DisabledAtUtc == null
                && (query.IncludeArchived || dataset.ArchivedAtUtc == null)
            )
            .OrderByDescending(dataset => dataset.UpdatedAtUtc)
            .ThenBy(dataset => dataset.Id)
            .ToArrayAsync(cancellationToken);

        if (datasets.Length == 0)
        {
            return [];
        }

        var datasetIds = datasets.Select(dataset => dataset.Id).ToArray();
        var counts = await db
            .CodeReviewEvaluationCases.TagWithOperationCallSite(
                "code_review_evaluation_dataset.list_case_counts"
            )
            .Where(item =>
                item.OrganizationId == query.OrganizationId
                && datasetIds.Contains(item.DatasetId)
                && item.RemovedAtUtc == null
            )
            .GroupBy(item => item.DatasetId)
            .Select(group => new { DatasetId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.DatasetId, row => row.Count, cancellationToken);

        return
        [
            .. datasets.Select(dataset => new CodeReviewEvaluationDatasetSummary(
                dataset,
                counts.GetValueOrDefault(dataset.Id, 0)
            )),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewEvaluationCase>> ListCasesAsync(
        string organizationId,
        string datasetId,
        bool includeRemoved,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewEvaluationCases.TagWithOperationCallSite("code_review_evaluation_case.list")
            .AsNoTracking()
            .Where(item =>
                item.OrganizationId == organizationId
                && item.DatasetId == datasetId
                && item.DisabledAtUtc == null
                && (includeRemoved || item.RemovedAtUtc == null)
            )
            .OrderBy(item => item.PullRequestNumber)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCase?> FindCaseAsync(
        string organizationId,
        string datasetId,
        string caseId,
        bool includeRemoved,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewEvaluationCases.TagWithOperationCallSite("code_review_evaluation_case.find")
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item =>
                    item.OrganizationId == organizationId
                    && item.DatasetId == datasetId
                    && item.Id == caseId
                    && item.DisabledAtUtc == null
                    && (includeRemoved || item.RemovedAtUtc == null),
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationDatasetSaveResult> SaveAsync(
        CodeReviewEvaluationDatasetSaveRequest request,
        CancellationToken cancellationToken
    )
    {
        var validationError = CodeReviewEvaluationCaseValidation.ValidateSaveRequest(request);
        if (validationError is not null)
        {
            return new(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase, null, null);
        }

        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationDatasetAsync(
            transaction.Transaction,
            request.OrganizationId,
            request.DatasetId,
            cancellationToken
        );

        // Drop any pre-lock tracked copies first. A handler that loaded the dataset earlier in
        // the same scope would otherwise be handed its stale instance by identity resolution,
        // and two same-version saves could both write version N+1.
        DetachTrackedEvaluationRows(request.OrganizationId, request.DatasetId);

        // Row lock before reading the version so two saves cannot both observe the winner's
        // predecessor version.
        await db
            .Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM code_review_evaluation_datasets
                WHERE organization_id = {request.OrganizationId} AND id = {request.DatasetId}
                FOR UPDATE
                """
            )
            .ToListAsync(cancellationToken);

        var dataset = await db
            .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                "code_review_evaluation_dataset.save_load"
            )
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == request.OrganizationId
                    && candidate.Id == request.DatasetId
                    && candidate.RepositoryId == request.RepositoryId
                    && candidate.DisabledAtUtc == null
                    && candidate.ArchivedAtUtc == null,
                cancellationToken
            );

        if (dataset is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationDatasetSaveOutcome.DatasetNotFound, null, null);
        }

        if (dataset.Version != request.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationDatasetSaveOutcome.VersionConflict, null, null);
        }

        var existingCases = await db
            .CodeReviewEvaluationCases.TagWithOperationCallSite(
                "code_review_evaluation_case.save_load"
            )
            .Where(item =>
                item.OrganizationId == request.OrganizationId
                && item.DatasetId == request.DatasetId
                && item.DisabledAtUtc == null
            )
            .ToArrayAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var desiredCaseIds = new HashSet<string>(StringComparer.Ordinal);

        // Promoted findings are operator-trusted content, but their provenance is not: an
        // organization admin could point a reference at another repository's run. Validate every
        // promoted source against this organization and repository before anything is written.
        if (
            !await PromotionSourcesAreValidAsync(
                request.OrganizationId,
                request.RepositoryId,
                request.Cases,
                cancellationToken
            )
        )
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase, null, null);
        }

        // Every listed case id must exist in this dataset and describe the same pull request.
        // Silently accepting either mismatch would let one draft rewrite another PR's
        // expectations.
        foreach (var requested in request.Cases.Where(item => item.CaseId is not null))
        {
            var existing = existingCases.FirstOrDefault(item => item.Id == requested.CaseId);
            if (existing is null || existing.PullRequestNumber != requested.PullRequestNumber)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase, null, null);
            }

            desiredCaseIds.Add(existing.Id);
        }

        foreach (var requestedCase in request.Cases)
        {
            if (requestedCase.CaseId is not null)
            {
                var existing = existingCases.First(item => item.Id == requestedCase.CaseId);
                desiredCaseIds.Add(existing.Id);

                if (requestedCase.Capture is not null)
                {
                    ApplyCapture(existing, requestedCase.Capture, now);

                    // Fresh capture invalidates every prior label: stale expectations must
                    // never attach to changed code. Explicit labels in the same draft apply
                    // only to brand-new membership; a refresh always resets.
                    existing.ReferenceSets = [];
                }
                else if (requestedCase.ReferenceSets is not null)
                {
                    existing.ReferenceSets = [.. requestedCase.ReferenceSets];
                }

                existing.RemovedAtUtc = null;
                existing.UpdatedAtUtc = now;
                continue;
            }

            // New or re-added membership. A soft-removed row keeps its case identity so the
            // curation draft and reference scope stay stable.
            var reAdd = existingCases.FirstOrDefault(item =>
                item.PullRequestNumber == requestedCase.PullRequestNumber
            );

            if (reAdd is not null)
            {
                ApplyCapture(reAdd, requestedCase.Capture!, now);
                // Never carry labels from before the removal onto a fresh capture.
                reAdd.ReferenceSets = requestedCase.ReferenceSets is null
                    ? []
                    : [.. requestedCase.ReferenceSets];
                reAdd.RemovedAtUtc = null;
                reAdd.UpdatedAtUtc = now;
                desiredCaseIds.Add(reAdd.Id);
                continue;
            }

            var created = new CodeReviewEvaluationCase
            {
                Id = $"crec_{Guid.CreateVersion7():N}",
                OrganizationId = request.OrganizationId,
                RepositoryId = request.RepositoryId,
                DatasetId = request.DatasetId,
                PullRequestNumber = requestedCase.PullRequestNumber,
                PullRequestTitle = string.Empty,
                SnapshotStorageUri = string.Empty,
                SnapshotSha256 = string.Empty,
                BaseSha = string.Empty,
                HeadSha = string.Empty,
                CapturedAtUtc = now,
                ReferenceSets = [],
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            ApplyCapture(created, requestedCase.Capture!, now);

            // A brand-new case has no stale labels to protect, so explicit labels entered in
            // the same draft are kept and persist in this single save.
            created.ReferenceSets = requestedCase.ReferenceSets is null
                ? []
                : [.. requestedCase.ReferenceSets];

            db.CodeReviewEvaluationCases.Add(created);
        }

        // Membership is the complete desired set: anything an active row no longer lists is
        // soft-removed, and its run history is untouched. Re-added rows are in the desired
        // set, so a remove-then-re-add save leaves the row active.
        foreach (var existing in existingCases)
        {
            if (existing.RemovedAtUtc is null && !desiredCaseIds.Contains(existing.Id))
            {
                existing.RemovedAtUtc = now;
                existing.UpdatedAtUtc = now;
            }
        }

        // Defense in depth: handlers reject over-limit drafts first, but the store must never
        // persist an unbounded set even if a caller bypasses validation.
        var resultingCaseCount = request.Cases.Count;
        if (resultingCaseCount > MaxCasesPerDataset)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase, null, null);
        }

        dataset.Name = request.Name.Trim();
        dataset.Description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();
        dataset.Version += 1;
        dataset.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var liveCases = await ListCasesAsync(
            request.OrganizationId,
            request.DatasetId,
            includeRemoved: false,
            cancellationToken
        );

        return new(CodeReviewEvaluationDatasetSaveOutcome.Saved, dataset, liveCases);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationDataset?> ArchiveAsync(
        string organizationId,
        string datasetId,
        CancellationToken cancellationToken
    )
    {
        // NOTE: the archive write must not depend on the change tracker. Reads in this store are
        // untracked, so mutating a returned entity would silently never persist. The update is a
        // single statement under the dataset advisory lock and is idempotent on an already
        // archived row.
        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationDatasetAsync(
            transaction.Transaction,
            organizationId,
            datasetId,
            cancellationToken
        );

        var now = timeProvider.GetUtcNow();
        var archived = await db
            .CodeReviewEvaluationDatasets.Where(dataset =>
                dataset.OrganizationId == organizationId
                && dataset.Id == datasetId
                && dataset.DisabledAtUtc == null
                && dataset.ArchivedAtUtc == null
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(dataset => dataset.ArchivedAtUtc, now)
                        .SetProperty(dataset => dataset.UpdatedAtUtc, now),
                cancellationToken
            );

        await transaction.CommitAsync(cancellationToken);

        if (archived == 0)
        {
            // Either the dataset does not exist, or it is already archived. Return the readable
            // row for the already-archived case, null only when it is genuinely absent.
            return await FindAsync(
                organizationId,
                datasetId,
                includeArchived: true,
                cancellationToken
            );
        }

        return await FindAsync(
            organizationId,
            datasetId,
            includeArchived: true,
            cancellationToken
        );
    }

    private async Task<bool> PromotionSourcesAreValidAsync(
        string organizationId,
        string repositoryId,
        IReadOnlyList<CodeReviewEvaluationCaseSave> cases,
        CancellationToken cancellationToken
    )
    {
        // Promotions are scoped to one source run AND one pull request. Candidate finding ids
        // repeat across runs and across pull requests, so a flat list would let one source claim
        // a finding that only exists elsewhere. The target case may live in another dataset for
        // the same PR, so the source result is matched by pull request number rather than by
        // dataset case id.
        var promoted = cases
            .SelectMany(item =>
                (item.ReferenceSets ?? []).SelectMany(set =>
                    set.Findings.Select(finding => new
                    {
                        item.PullRequestNumber,
                        Finding = finding,
                    })
                )
            )
            .Where(entry => entry.Finding.Origin == CodeReviewEvaluationReferenceOrigin.RunFinding)
            .ToArray();

        if (promoted.Length == 0)
        {
            return true;
        }

        var runIds = promoted
            .Select(entry => entry.Finding.SourceRunId)
            .Where(runId => !string.IsNullOrWhiteSpace(runId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (runIds.Length == 0)
        {
            return false;
        }

        var ownedRuns = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_dataset.promotion_runs"
            )
            .AsNoTracking()
            .Where(run =>
                run.OrganizationId == organizationId
                && run.RepositoryId == repositoryId
                && runIds.Contains(run.Id)
                && run.DisabledAtUtc == null
            )
            .Select(run => run.Id)
            .ToArrayAsync(cancellationToken);

        var ownedRunIds = ownedRuns.ToHashSet(StringComparer.Ordinal);
        if (promoted.Any(entry => !ownedRunIds.Contains(entry.Finding.SourceRunId ?? string.Empty)))
        {
            return false;
        }

        var pullRequestNumbers = promoted
            .Select(entry => entry.PullRequestNumber)
            .Distinct()
            .ToArray();

        var candidates = await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_dataset.promotion_findings"
            )
            .AsNoTracking()
            .Where(result =>
                result.OrganizationId == organizationId
                && runIds.Contains(result.RunId)
                && pullRequestNumbers.Contains(result.PullRequestNumber)
                && result.CandidateFindingsJson != null
                && result.DisabledAtUtc == null
            )
            .Select(result => new
            {
                result.RunId,
                result.PullRequestNumber,
                Payload = result.CandidateFindingsJson!,
            })
            .ToArrayAsync(cancellationToken);

        return promoted.All(entry =>
            !string.IsNullOrWhiteSpace(entry.Finding.SourceFindingId)
            && candidates.Any(candidate =>
                string.Equals(
                    candidate.RunId,
                    entry.Finding.SourceRunId,
                    StringComparison.Ordinal
                )
                && candidate.PullRequestNumber == entry.PullRequestNumber
                && CandidateFindingsContain(candidate.Payload, entry.Finding.SourceFindingId)
            )
        );
    }

    private static bool CandidateFindingsContain(string payload, string findingId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Array
                && document
                    .RootElement.EnumerateArray()
                    .Any(element =>
                        element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("id", out var id)
                        && string.Equals(id.GetString(), findingId, StringComparison.Ordinal)
                    );
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void DetachTrackedEvaluationRows(string organizationId, string datasetId)
    {
        foreach (
            var entry in db
                .ChangeTracker.Entries<CodeReviewEvaluationDataset>()
                .Where(candidate =>
                    candidate.Entity.OrganizationId == organizationId
                    && candidate.Entity.Id == datasetId
                )
                .ToArray()
        )
        {
            entry.State = EntityState.Detached;
        }

        foreach (
            var entry in db
                .ChangeTracker.Entries<CodeReviewEvaluationCase>()
                .Where(candidate =>
                    candidate.Entity.OrganizationId == organizationId
                    && candidate.Entity.DatasetId == datasetId
                )
                .ToArray()
        )
        {
            entry.State = EntityState.Detached;
        }
    }

    private static void ApplyCapture(
        CodeReviewEvaluationCase item,
        CodeReviewEvaluationStagedCapture capture,
        DateTimeOffset now
    )
    {
        item.PullRequestTitle = capture.PullRequestTitle;
        item.SnapshotStorageUri = capture.Snapshot.StorageUri;
        item.SnapshotSha256 = capture.Snapshot.Sha256;
        item.SnapshotByteCount = capture.Snapshot.ByteCount;
        item.SnapshotSchemaVersion = CodeReviewEvaluationSnapshot.CurrentSchemaVersion;
        item.BaseSha = capture.BaseSha;
        item.HeadSha = capture.HeadSha;
        item.PullRequestState = capture.State;
        item.CapturedAtUtc = capture.CapturedAtUtc;
        item.ChangedFileCount = capture.ChangedFileCount;
        item.OmittedFileCount = capture.OmittedFileCount;
        item.DeclaredChangedFileCount = capture.DeclaredChangedFileCount;
        item.PullRequestRecordId = capture.PullRequestRecordId;
        item.UpdatedAtUtc = now;
    }
}
