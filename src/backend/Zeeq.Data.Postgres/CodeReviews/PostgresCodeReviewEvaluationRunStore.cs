using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for evaluation run admission and ownership.
/// </summary>
/// <remarks>
/// Lock order is fixed: organization advisory lock, then dataset row, then run row. Every
/// ownership transition is a single SQL statement that checks state, lease token and lease
/// expiry, so a duplicate delivery or a stale worker cannot advance another owner's run.
/// Reads are untracked on purpose: after a fenced SQL write, a tracked entity would replay
/// stale lease and state values and misjudge ownership. No transaction here spans a provider
/// call or an artifact write.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationRunStore(
    PostgresDbContext db,
    TimeProvider timeProvider
) : ICodeReviewEvaluationRunStore
{
    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunAdmissionResult> AdmitAsync(
        CodeReviewEvaluationRunAdmission request,
        CancellationToken cancellationToken
    )
    {
        var selectionError = ValidateSelection(request.ReviewerSelection)
            ?? ValidateSelection(request.JudgeSelection);
        if (selectionError is not null)
        {
            throw new ArgumentException(selectionError, nameof(request));
        }

        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationAdmissionAsync(
            transaction.Transaction,
            request.OrganizationId,
            cancellationToken
        );

        var existingRequest = await FindByRequestKeyAsync(
            request.OrganizationId,
            request.RequestKey,
            cancellationToken
        );
        if (existingRequest is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey,
                existingRequest.Run,
                null
            );
        }

        var activeRunId = await FindActiveRunIdAsync(request.OrganizationId, cancellationToken);
        if (activeRunId is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict,
                null,
                activeRunId
            );
        }

        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationDatasetAsync(
            transaction.Transaction,
            request.OrganizationId,
            request.DatasetId,
            cancellationToken
        );

        var datasetLocked = await LockDatasetAsync(
            request.OrganizationId,
            request.DatasetId,
            cancellationToken
        );
        if (!datasetLocked)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationRunAdmissionOutcome.DatasetNotFound, null, null);
        }

        var dataset = await db
            .CodeReviewEvaluationDatasets.TagWithOperationCallSite(
                "code_review_evaluation_run.admit_dataset"
            )
            .AsNoTracking()
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
            return new(CodeReviewEvaluationRunAdmissionOutcome.DatasetNotFound, null, null);
        }

        if (dataset.Version != request.ExpectedDatasetVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationRunAdmissionOutcome.DatasetVersionConflict, null, null);
        }

        var cases = await LoadManifestCasesAsync(
            request.OrganizationId,
            request.DatasetId,
            request.AgentId,
            cancellationToken
        );
        if (cases.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationRunAdmissionOutcome.NoRunnableCases, null, null);
        }

        var now = timeProvider.GetUtcNow();
        var manifest = new CodeReviewEvaluationRunManifest(
            CodeReviewEvaluationRunManifest.CurrentSchemaVersion,
            request.OrganizationId,
            request.RepositoryId,
            request.DatasetId,
            dataset.Version,
            request.AgentId,
            request.ReviewerConfiguration.DisplayName,
            request.ReviewerConfiguration.ReviewFacet,
            request.SavedRevisionId,
            request.ReviewerConfiguration,
            request.ReviewerSelection,
            request.JudgeSelection,
            request.JudgeRubricVersion,
            request.SharedSystemPrompt,
            request.SharedSystemPromptHash,
            request.RepositoryConfigurationJson,
            request.LibraryIds,
            request.BuildVersion,
            request.SystemPromptVersion,
            request.RequestedByUserId,
            request.RequestFingerprint,
            now,
            null,
            cases
        );

        var run = CreateRun(request, dataset, manifest, now);
        db.CodeReviewEvaluationRuns.Add(run);
        AddPendingCaseResults(run, cases, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(CodeReviewEvaluationRunAdmissionOutcome.Admitted, run, null);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunAdmissionResult> AdmitRetryAsync(
        CodeReviewEvaluationRunRetryAdmission request,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationAdmissionAsync(
            transaction.Transaction,
            request.OrganizationId,
            cancellationToken
        );

        var existingRequest = await FindByRequestKeyAsync(
            request.OrganizationId,
            request.RequestKey,
            cancellationToken
        );
        if (existingRequest is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey,
                existingRequest.Run,
                null
            );
        }

        var activeRunId = await FindActiveRunIdAsync(request.OrganizationId, cancellationToken);
        if (activeRunId is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict,
                null,
                activeRunId
            );
        }

        var parentDetail = await FindAsync(
            request.OrganizationId,
            request.ParentRunId,
            cancellationToken
        );
        if (parentDetail is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationRunAdmissionOutcome.DatasetNotFound, null, null);
        }

        var parentResults = await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_run.retry_parent_results"
            )
            .AsNoTracking()
            .Where(result =>
                result.OrganizationId == request.OrganizationId
                && result.RunId == request.ParentRunId
                && result.DisabledAtUtc == null
            )
            .ToArrayAsync(cancellationToken);

        // Failed and interrupted cases are retryable. A cancelled case is interrupted: an
        // abandoned or cancelled parent converts every non-terminal case to Cancelled, so
        // excluding it would leave crash recovery with nothing to run. Graded, skipped and
        // unlabelled cases are never retried.
        var runnableCaseIds = parentResults
            .Where(result =>
                result.Phase != CodeReviewEvaluationCasePhase.Graded
                && result.Outcome
                    is CodeReviewEvaluationCaseOutcome.ReviewFailed
                        or CodeReviewEvaluationCaseOutcome.JudgeFailed
                        or CodeReviewEvaluationCaseOutcome.None
                        or CodeReviewEvaluationCaseOutcome.Cancelled
            )
            .Select(result => result.ManifestCaseId)
            .ToHashSet(StringComparer.Ordinal);

        var retryCases = parentDetail
            .Manifest.Cases.Where(item => runnableCaseIds.Contains(item.CaseId))
            .ToArray();

        if (retryCases.Length == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CodeReviewEvaluationRunAdmissionOutcome.NoRunnableCases, null, null);
        }

        var now = timeProvider.GetUtcNow();
        var parent = parentDetail.Run;
        var manifest = parentDetail.Manifest with
        {
            AdmittedBuildVersion = request.BuildVersion,
            AdmittedSystemPromptVersion = request.SystemPromptVersion,
            RequestedByUserId = request.RequestedByUserId,
            RequestFingerprint = request.RequestFingerprint,
            AdmittedAtUtc = now,
            ParentRunId = parent.Id,
            Cases = retryCases,
        };

        var run = new CodeReviewEvaluationRun
        {
            Id = $"cerev_{Guid.CreateVersion7():N}",
            OrganizationId = parent.OrganizationId,
            TeamId = parent.TeamId,
            RepositoryId = parent.RepositoryId,
            DatasetId = parent.DatasetId,
            DatasetVersion = parent.DatasetVersion,
            AgentId = parent.AgentId,
            SavedRevisionId = parent.SavedRevisionId,
            ManifestJson = CodeReviewEvaluationRunManifestSerializer.Serialize(manifest),
            RequestedByUserId = request.RequestedByUserId,
            State = CodeReviewEvaluationRunState.Queued,
            ParentRunId = parent.Id,
            RequestKey = request.RequestKey,
            CaseCount = retryCases.Length,
            UnlabelledCaseCount = retryCases.Count(item =>
                item.References.State == CodeReviewEvaluationReferenceState.Unlabelled
            ),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.CodeReviewEvaluationRuns.Add(run);
        AddPendingCaseResults(run, retryCases, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(CodeReviewEvaluationRunAdmissionOutcome.Admitted, run, null);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunDetail?> FindAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var run = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite("code_review_evaluation_run.find")
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == organizationId
                    && candidate.Id == runId
                    && candidate.DisabledAtUtc == null,
                cancellationToken
            );

        return run is null
            ? null
            : new(run, CodeReviewEvaluationRunManifestSerializer.Deserialize(run.ManifestJson));
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunDetail?> FindByRequestKeyAsync(
        string organizationId,
        string requestKey,
        CancellationToken cancellationToken
    )
    {
        var run = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_run.find_by_request_key"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == organizationId
                    && candidate.RequestKey == requestKey
                    && candidate.DisabledAtUtc == null,
                cancellationToken
            );

        return run is null
            ? null
            : new(run, CodeReviewEvaluationRunManifestSerializer.Deserialize(run.ManifestJson));
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunPage> ListAsync(
        CodeReviewEvaluationRunQuery query,
        CancellationToken cancellationToken
    )
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var runs = db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite("code_review_evaluation_run.list")
            .AsNoTracking()
            .Where(run =>
                run.OrganizationId == query.OrganizationId
                && run.RepositoryId == query.RepositoryId
                && run.DisabledAtUtc == null
            );

        if (!string.IsNullOrWhiteSpace(query.DatasetId))
        {
            runs = runs.Where(run => run.DatasetId == query.DatasetId);
        }

        if (!string.IsNullOrWhiteSpace(query.CursorId))
        {
            var cursorId = query.CursorId;
            runs = runs.Where(run => run.Id.CompareTo(cursorId) < 0);
        }

        var items = await runs.OrderByDescending(run => run.Id)
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken);

        var hasMore = items.Length > pageSize;
        var page = hasMore ? items[..pageSize] : items;

        return new(page, hasMore ? page[^1].Id : null);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRunClaimResult> TryClaimAsync(
        string organizationId,
        string runId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        // Lease expiry is created from the DATABASE clock. Computing it from the replica clock
        // grants a skewed process extra lease time (or an already-expired lease) and defeats
        // the fenced writes that compare against now().
        var claimed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE zeeq.code_review_evaluation_runs
            SET state = 'Running',
                lease_token = @token,
                lease_expires_at_utc = now() + @duration,
                started_at_utc = COALESCE(started_at_utc, now()),
                updated_at_utc = now()
            WHERE organization_id = @org
              AND id = @run
              AND state = 'Queued'
            """,
            [
                new NpgsqlParameter("org", organizationId),
                new NpgsqlParameter("run", runId),
                new NpgsqlParameter("token", leaseToken),
                new NpgsqlParameter("duration", NpgsqlDbType.Interval) { Value = leaseDuration },
            ],
            cancellationToken
        );

        var detail = await FindAsync(organizationId, runId, cancellationToken);
        if (detail is null)
        {
            return new(CodeReviewEvaluationRunClaimOutcome.NotRunnable, null, null);
        }

        if (claimed > 0)
        {
            return new(CodeReviewEvaluationRunClaimOutcome.Claimed, detail.Run, detail.Manifest);
        }

        var run = detail.Run;
        if (
            run.State == CodeReviewEvaluationRunState.Running
            && string.Equals(run.LeaseToken, leaseToken, StringComparison.Ordinal)
        )
        {
            // An expired lease is not "already owned": the owner has lost the run and must not
            // be told it still holds it.
            return await IsLeaseLiveAsync(organizationId, runId, leaseToken, cancellationToken)
                ? new(CodeReviewEvaluationRunClaimOutcome.AlreadyOwned, run, detail.Manifest)
                : new(CodeReviewEvaluationRunClaimOutcome.NotRunnable, run, detail.Manifest);
        }

        if (run.State == CodeReviewEvaluationRunState.Running)
        {
            var live = await db
                .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                    "code_review_evaluation_run.claim_lease_live"
                )
                .AsNoTracking()
                .AnyAsync(
                    candidate =>
                        candidate.OrganizationId == organizationId
                        && candidate.Id == runId
                        && candidate.State == CodeReviewEvaluationRunState.Running
                        && candidate.LeaseExpiresAtUtc != null
                        // NOTE: DateTimeOffset.UtcNow translates to the database clock, so a
                        // replica with a skewed local clock cannot steal a live lease.
                        && candidate.LeaseExpiresAtUtc > DateTimeOffset.UtcNow,
                    cancellationToken
                );

            return live
                ? new(CodeReviewEvaluationRunClaimOutcome.LeaseActive, run, detail.Manifest)
                : new(CodeReviewEvaluationRunClaimOutcome.NotRunnable, run, detail.Manifest);
        }

        return new(CodeReviewEvaluationRunClaimOutcome.NotRunnable, run, detail.Manifest);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationLeaseRenewal> RenewLeaseAsync(
        string organizationId,
        string runId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        // Both the validity check and the new expiry use the database clock, so a replica with
        // a skewed clock can neither steal a live lease nor extend its own beyond the duration.
        var renewed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE zeeq.code_review_evaluation_runs
            SET lease_expires_at_utc = now() + @duration,
                updated_at_utc = now()
            WHERE organization_id = @org
              AND id = @run
              AND state = 'Running'
              AND lease_token = @token
              AND lease_expires_at_utc IS NOT NULL
              AND lease_expires_at_utc > now()
            """,
            [
                new NpgsqlParameter("org", organizationId),
                new NpgsqlParameter("run", runId),
                new NpgsqlParameter("token", leaseToken),
                new NpgsqlParameter("duration", NpgsqlDbType.Interval) { Value = leaseDuration },
            ],
            cancellationToken
        );

        if (renewed == 0)
        {
            return new(false, false);
        }

        var cancelRequested = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_run.renew_cancel_check"
            )
            .AsNoTracking()
            .Where(run => run.OrganizationId == organizationId && run.Id == runId)
            .Select(run => run.CancelRequestedAtUtc != null)
            .FirstOrDefaultAsync(cancellationToken);

        return new(true, cancelRequested);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRun> MarkDispatchFailedAsync(
        string organizationId,
        string runId,
        string errorCode,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        // Only a still-queued run may be marked dispatch-failed. An ambiguous publish that a
        // consumer already claimed must never be overwritten here.
        await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_run.mark_dispatch_failed"
            )
            .Where(run =>
                run.OrganizationId == organizationId
                && run.Id == runId
                && run.State == CodeReviewEvaluationRunState.Queued
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(run => run.State, CodeReviewEvaluationRunState.DispatchFailed)
                        .SetProperty(run => run.ErrorCode, errorCode)
                        .SetProperty(run => run.CompletedAtUtc, now)
                        .SetProperty(run => run.UpdatedAtUtc, now),
                cancellationToken
            );

        return await LoadRunAsync(organizationId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRun> RequestCancelAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationAdmissionAsync(
            transaction.Transaction,
            organizationId,
            cancellationToken
        );

        var run = await LockRunAsync(organizationId, runId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (run.State == CodeReviewEvaluationRunState.Queued)
        {
            // No consumer owns the run yet, so cancellation is terminal immediately and
            // admission is freed in the same transaction.
            await db
                .CodeReviewEvaluationRuns.Where(candidate =>
                    candidate.OrganizationId == organizationId
                    && candidate.Id == runId
                    && candidate.State == CodeReviewEvaluationRunState.Queued
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(
                                candidate => candidate.State,
                                CodeReviewEvaluationRunState.Cancelled
                            )
                            .SetProperty(candidate => candidate.CancelRequestedAtUtc, now)
                            .SetProperty(candidate => candidate.CompletedAtUtc, now)
                            .SetProperty(candidate => candidate.UpdatedAtUtc, now),
                    cancellationToken
                );

            await CancelInFlightCasesAsync(organizationId, runId, now, cancellationToken);
        }
        else if (run.State == CodeReviewEvaluationRunState.Running)
        {
            // The owner keeps its lease so finished results survive; new case starts are
            // rejected from now on.
            await db
                .CodeReviewEvaluationRuns.Where(candidate =>
                    candidate.OrganizationId == organizationId
                    && candidate.Id == runId
                    && candidate.State == CodeReviewEvaluationRunState.Running
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(
                                candidate => candidate.CancelRequestedAtUtc,
                                candidate => candidate.CancelRequestedAtUtc ?? now
                            )
                            .SetProperty(candidate => candidate.UpdatedAtUtc, now),
                    cancellationToken
                );
        }

        await transaction.CommitAsync(cancellationToken);

        return await LoadRunAsync(organizationId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRun> AbandonAsync(
        string organizationId,
        string runId,
        string errorCode,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationAdmissionAsync(
            transaction.Transaction,
            organizationId,
            cancellationToken
        );

        var run = await LockRunAsync(organizationId, runId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Already terminal: abandon is a no-op so a duplicate operator action cannot discard
        // successful results.
        if (IsTerminal(run.State))
        {
            await transaction.CommitAsync(cancellationToken);
            return run;
        }

        // A live Running owner must not be abandoned: freeing admission here would let a
        // second run overlap its in-flight provider calls. Cancel first, or wait for expiry.
        if (
            run.State == CodeReviewEvaluationRunState.Running
            && run.LeaseToken is { } abandonToken
            && await IsLeaseLiveAsync(organizationId, runId, abandonToken, cancellationToken)
        )
        {
            throw new CodeReviewEvaluationFencedException(
                "lease_active",
                "The evaluation run still has a live lease and cannot be abandoned."
            );
        }

        await CancelInFlightCasesAsync(organizationId, runId, now, cancellationToken);
        var counts = await RecomputeCountsAsync(organizationId, runId, cancellationToken);

        await db
            .CodeReviewEvaluationRuns.Where(candidate =>
                candidate.OrganizationId == organizationId && candidate.Id == runId
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(
                            candidate => candidate.State,
                            CodeReviewEvaluationRunState.Abandoned
                        )
                        .SetProperty(candidate => candidate.ErrorCode, errorCode)
                        .SetProperty(candidate => candidate.FailureMessage, (string?)null)
                        .SetProperty(candidate => candidate.CaseCount, counts.CaseCount)
                        .SetProperty(candidate => candidate.GradedCaseCount, counts.GradedCaseCount)
                        .SetProperty(
                            candidate => candidate.SkippedCaseCount,
                            counts.SkippedCaseCount
                        )
                        .SetProperty(candidate => candidate.FailedCaseCount, counts.FailedCaseCount)
                        .SetProperty(
                            candidate => candidate.UnlabelledCaseCount,
                            counts.UnlabelledCaseCount
                        )
                        .SetProperty(
                            candidate => candidate.MatchedReferenceCount,
                            counts.MatchedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.MissedReferenceCount,
                            counts.MissedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.ExpectedReferenceCount,
                            counts.ExpectedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.AdditionalFindingCount,
                            counts.AdditionalFindingCount
                        )
                        .SetProperty(candidate => candidate.CompletedAtUtc, now)
                        .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                        .SetProperty(candidate => candidate.LeaseToken, (string?)null)
                        .SetProperty(
                            candidate => candidate.LeaseExpiresAtUtc,
                            (DateTimeOffset?)null
                        ),
                cancellationToken
            );

        await transaction.CommitAsync(cancellationToken);

        return await LoadRunAsync(organizationId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationRun> FinalizeAsync(
        CodeReviewEvaluationRunFinalize request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.State
            is not (
                CodeReviewEvaluationRunState.Completed
                or CodeReviewEvaluationRunState.Incomplete
                or CodeReviewEvaluationRunState.Cancelled
                or CodeReviewEvaluationRunState.Abandoned
            )
        )
        {
            throw new ArgumentException(
                "Finalize accepts only Completed, Incomplete, Cancelled or Abandoned.",
                nameof(request)
            );
        }

        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireEvaluationAdmissionAsync(
            transaction.Transaction,
            request.OrganizationId,
            cancellationToken
        );

        var run = await LockRunAsync(request.OrganizationId, request.RunId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var owned =
            run.State == CodeReviewEvaluationRunState.Running
            && string.Equals(run.LeaseToken, request.LeaseToken, StringComparison.Ordinal)
            && await IsLeaseLiveAsync(
                request.OrganizationId,
                request.RunId,
                request.LeaseToken,
                cancellationToken
            );

        if (!owned)
        {
            throw new CodeReviewEvaluationFencedException(
                "lease_not_owned",
                "The run lease is not owned by this worker or has expired."
            );
        }

        if (
            request.State == CodeReviewEvaluationRunState.Cancelled
            && run.CancelRequestedAtUtc is null
        )
        {
            throw new CodeReviewEvaluationFencedException(
                "cancel_not_requested",
                "A run can only finalize as cancelled after a cancellation request."
            );
        }

        await CancelInFlightCasesAsync(
            request.OrganizationId,
            request.RunId,
            now,
            cancellationToken
        );
        var counts = await RecomputeCountsAsync(
            request.OrganizationId,
            request.RunId,
            cancellationToken
        );

        // A cancellation that arrives between the last case finishing and this call must win.
        // The row lock and the shared admission lock make this decision atomic, so a run never
        // reports Completed after its owner observed a cancel request.
        var terminalState =
            run.CancelRequestedAtUtc is not null
            && request.State
                is CodeReviewEvaluationRunState.Completed
                    or CodeReviewEvaluationRunState.Incomplete
                ? CodeReviewEvaluationRunState.Cancelled
                : request.State;

        await db
            .CodeReviewEvaluationRuns.Where(candidate =>
                candidate.OrganizationId == request.OrganizationId && candidate.Id == request.RunId
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(candidate => candidate.State, terminalState)
                        .SetProperty(candidate => candidate.ErrorCode, request.ErrorCode)
                        .SetProperty(candidate => candidate.FailureMessage, request.FailureMessage)
                        .SetProperty(candidate => candidate.CaseCount, counts.CaseCount)
                        .SetProperty(candidate => candidate.GradedCaseCount, counts.GradedCaseCount)
                        .SetProperty(
                            candidate => candidate.SkippedCaseCount,
                            counts.SkippedCaseCount
                        )
                        .SetProperty(candidate => candidate.FailedCaseCount, counts.FailedCaseCount)
                        .SetProperty(
                            candidate => candidate.UnlabelledCaseCount,
                            counts.UnlabelledCaseCount
                        )
                        .SetProperty(
                            candidate => candidate.MatchedReferenceCount,
                            counts.MatchedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.MissedReferenceCount,
                            counts.MissedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.ExpectedReferenceCount,
                            counts.ExpectedReferenceCount
                        )
                        .SetProperty(
                            candidate => candidate.AdditionalFindingCount,
                            counts.AdditionalFindingCount
                        )
                        .SetProperty(candidate => candidate.CompletedAtUtc, now)
                        .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                        .SetProperty(candidate => candidate.LeaseToken, (string?)null)
                        .SetProperty(
                            candidate => candidate.LeaseExpiresAtUtc,
                            (DateTimeOffset?)null
                        ),
                cancellationToken
            );

        await transaction.CommitAsync(cancellationToken);

        return await LoadRunAsync(request.OrganizationId, request.RunId, cancellationToken);
    }

    private Task<bool> IsLeaseLiveAsync(
        string organizationId,
        string runId,
        string leaseToken,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_run.lease_live"
            )
            .AsNoTracking()
            .AnyAsync(
                run =>
                    run.OrganizationId == organizationId
                    && run.Id == runId
                    && run.State == CodeReviewEvaluationRunState.Running
                    && run.LeaseToken == leaseToken
                    && run.LeaseExpiresAtUtc != null
                    // Database clock: a renewed lease from another replica must count even if
                    // this process's clock drifted.
                    && run.LeaseExpiresAtUtc > DateTimeOffset.UtcNow,
                cancellationToken
            );

    private static bool IsTerminal(CodeReviewEvaluationRunState state) =>
        state
            is CodeReviewEvaluationRunState.Completed
                or CodeReviewEvaluationRunState.Incomplete
                or CodeReviewEvaluationRunState.Cancelled
                or CodeReviewEvaluationRunState.Abandoned
                or CodeReviewEvaluationRunState.DispatchFailed;

    private async Task<string?> FindActiveRunIdAsync(
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_run.find_active"
            )
            .AsNoTracking()
            .Where(run =>
                run.OrganizationId == organizationId
                && (
                    run.State == CodeReviewEvaluationRunState.Queued
                    || run.State == CodeReviewEvaluationRunState.Running
                )
            )
            .Select(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<bool> LockDatasetAsync(
        string organizationId,
        string datasetId,
        CancellationToken cancellationToken
    )
    {
        var rows = await db
            .Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM code_review_evaluation_datasets
                WHERE organization_id = {organizationId} AND id = {datasetId}
                FOR UPDATE
                """
            )
            .ToListAsync(cancellationToken);

        return rows.Count > 0;
    }

    private async Task<CodeReviewEvaluationRun> LockRunAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    )
    {
        await db
            .Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM code_review_evaluation_runs
                WHERE organization_id = {organizationId} AND id = {runId}
                FOR UPDATE
                """
            )
            .ToListAsync(cancellationToken);

        return await LoadRunAsync(organizationId, runId, cancellationToken);
    }

    private async Task<CodeReviewEvaluationRun> LoadRunAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite("code_review_evaluation_run.load")
            .AsNoTracking()
            .FirstOrDefaultAsync(
                run => run.OrganizationId == organizationId && run.Id == runId,
                cancellationToken
            )
        ?? throw new InvalidOperationException(
            "The requested evaluation run was not found for this organization."
        );

    private async Task<IReadOnlyList<CodeReviewEvaluationManifestCase>> LoadManifestCasesAsync(
        string organizationId,
        string datasetId,
        string agentId,
        CancellationToken cancellationToken
    )
    {
        var cases = await db
            .CodeReviewEvaluationCases.TagWithOperationCallSite(
                "code_review_evaluation_run.admit_cases"
            )
            .AsNoTracking()
            .Where(item =>
                item.OrganizationId == organizationId
                && item.DatasetId == datasetId
                && item.RemovedAtUtc == null
                && item.DisabledAtUtc == null
            )
            .OrderBy(item => item.PullRequestNumber)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);

        return
        [
            .. cases.Select(item => new CodeReviewEvaluationManifestCase(
                item.Id,
                item.PullRequestNumber,
                item.PullRequestTitle,
                new CodeReviewEvaluationArtifact(
                    item.SnapshotStorageUri,
                    item.SnapshotSha256,
                    item.SnapshotByteCount
                ),
                item.BaseSha,
                item.HeadSha,
                item.CapturedAtUtc,
                item.ChangedFileCount,
                item.OmittedFileCount,
                item.ReferenceSets.FirstOrDefault(set =>
                    string.Equals(set.ReviewerAgentId, agentId, StringComparison.Ordinal)
                ) ?? CodeReviewEvaluationReferenceSet.Unlabelled(agentId)
            )),
        ];
    }

    private static CodeReviewEvaluationRun CreateRun(
        CodeReviewEvaluationRunAdmission request,
        CodeReviewEvaluationDataset dataset,
        CodeReviewEvaluationRunManifest manifest,
        DateTimeOffset now
    ) =>
        new()
        {
            Id = $"ceref_{Guid.CreateVersion7():N}",
            OrganizationId = request.OrganizationId,
            TeamId = dataset.TeamId,
            RepositoryId = request.RepositoryId,
            DatasetId = dataset.Id,
            DatasetVersion = dataset.Version,
            AgentId = request.AgentId,
            SavedRevisionId = request.SavedRevisionId,
            ManifestJson = CodeReviewEvaluationRunManifestSerializer.Serialize(manifest),
            RequestedByUserId = request.RequestedByUserId,
            State = CodeReviewEvaluationRunState.Queued,
            RequestKey = request.RequestKey,
            CaseCount = manifest.Cases.Count,
            UnlabelledCaseCount = manifest.Cases.Count(item =>
                item.References.State == CodeReviewEvaluationReferenceState.Unlabelled
            ),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

    private void AddPendingCaseResults(
        CodeReviewEvaluationRun run,
        IReadOnlyList<CodeReviewEvaluationManifestCase> cases,
        DateTimeOffset now
    )
    {
        foreach (var item in cases)
        {
            db.CodeReviewEvaluationCaseResults.Add(
                new CodeReviewEvaluationCaseResult
                {
                    Id = $"ceres_{Guid.CreateVersion7():N}",
                    OrganizationId = run.OrganizationId,
                    RunId = run.Id,
                    ManifestCaseId = item.CaseId,
                    PullRequestNumber = item.PullRequestNumber,
                    Phase = CodeReviewEvaluationCasePhase.Pending,
                    Outcome = CodeReviewEvaluationCaseOutcome.None,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }
            );
        }
    }

    private async Task CancelInFlightCasesAsync(
        string organizationId,
        string runId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_case_result.cancel_in_flight"
            )
            .Where(result =>
                result.OrganizationId == organizationId
                && result.RunId == runId
                && result.Outcome == CodeReviewEvaluationCaseOutcome.None
                && result.DisabledAtUtc == null
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(
                            result => result.Outcome,
                            CodeReviewEvaluationCaseOutcome.Cancelled
                        )
                        .SetProperty(result => result.CompletedAtUtc, now)
                        .SetProperty(result => result.UpdatedAtUtc, now),
                cancellationToken
            );

    private async Task<CodeReviewEvaluationRunCounts> RecomputeCountsAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    )
    {
        var results = await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_case_result.recompute_counts"
            )
            .AsNoTracking()
            .Where(result =>
                result.OrganizationId == organizationId
                && result.RunId == runId
                && result.DisabledAtUtc == null
            )
            .ToArrayAsync(cancellationToken);

        // Counts are derived, never trusted from the caller. Reference counts come only from
        // graded cases; unlabelled cases never shrink the coverage denominator.
        var graded = results
            .Where(result => result.Outcome == CodeReviewEvaluationCaseOutcome.Graded)
            .ToArray();

        return new(
            results.Length,
            graded.Length,
            results.Count(result => result.Outcome == CodeReviewEvaluationCaseOutcome.Skipped),
            results.Count(result =>
                result.Outcome
                    is CodeReviewEvaluationCaseOutcome.ReviewFailed
                        or CodeReviewEvaluationCaseOutcome.JudgeFailed
            ),
            results.Count(result => result.Outcome == CodeReviewEvaluationCaseOutcome.Unlabelled),
            graded.Sum(result => result.MatchedReferenceCount),
            graded.Sum(result => result.MissedReferenceCount),
            graded.Sum(result => result.ExpectedReferenceCount),
            graded.Sum(result => result.AdditionalFindingCount)
        );
    }

    private static string? ValidateSelection(CodeReviewEvaluationModelSelection selection)
    {
        if (
            string.IsNullOrWhiteSpace(selection.Provider)
            || string.IsNullOrWhiteSpace(selection.Model)
        )
        {
            return "A frozen model selection requires a provider and model.";
        }

        return selection.CredentialSource switch
        {
            CodeReviewEvaluationCredentialSource.OrganizationManagedKey
                when string.IsNullOrWhiteSpace(selection.OrganizationKeyId) =>
                "An organization-managed credential source requires a key id.",
            CodeReviewEvaluationCredentialSource.SystemDefault
            or CodeReviewEvaluationCredentialSource.InternalDefaultKey
                when !string.IsNullOrWhiteSpace(selection.OrganizationKeyId) =>
                "Only an organization-managed credential source may carry a key id.",
            _ => null,
        };
    }
}
