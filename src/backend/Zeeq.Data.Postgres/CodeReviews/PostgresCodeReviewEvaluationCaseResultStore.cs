using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for per-case evaluation results.
/// </summary>
/// <remarks>
/// Every transition is one SQL statement that checks the owning run's state, lease token,
/// lease expiry, cancellation flag and expected case phase. A zero-row update is diagnosed
/// into a precise fenced reason instead of being retried. Candidate artifacts are written to
/// storage before this store records them, so a judge failure never loses reviewer output and
/// a retry never has to call the reviewer again.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationCaseResultStore(
    PostgresDbContext db,
    TimeProvider timeProvider
) : ICodeReviewEvaluationCaseResultStore
{
    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> StartReviewAsync(
        CodeReviewEvaluationCaseStart request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Reviewing', started_at_utc = @now, updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Pending'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: true)}
            """,
            BaseParameters(
                request.OrganizationId,
                request.RunId,
                request.CaseId,
                request.LeaseToken,
                now
            ),
            null,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordCandidateAsync(
        CodeReviewEvaluationCaseCandidate request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();
        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        AddArtifactParameters(parameters, request.CandidateArtifact);
        Add(parameters, "candidate_finding_count", request.CandidateFindingCount);
        AddJsonb(parameters, "candidate_findings_json", request.CandidateFindingsJson);
        AddJsonb(parameters, "reviewer_provenance_json", request.ReviewerProvenanceJson);
        AddBigint(parameters, "reviewer_input_tokens", request.InputTokens);
        AddBigint(parameters, "reviewer_output_tokens", request.OutputTokens);
        AddNumeric(parameters, "estimated_cost_usd", request.EstimatedCostUsd);

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Reviewed',
                outcome = 'None',
                candidate_storage_uri = @candidate_storage_uri,
                candidate_sha256 = @candidate_sha256,
                candidate_byte_count = @candidate_byte_count,
                candidate_finding_count = @candidate_finding_count,
                candidate_findings_json = @candidate_findings_json,
                reviewer_provenance_json = @reviewer_provenance_json,
                reviewer_input_tokens = @reviewer_input_tokens,
                reviewer_output_tokens = @reviewer_output_tokens,
                estimated_cost_usd = @estimated_cost_usd,
                updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Reviewing'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: false)}
            """,
            parameters,
            null,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordUnlabelledAsync(
        CodeReviewEvaluationCaseUnlabelled request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();
        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        AddArtifactParameters(parameters, request.CandidateArtifact);
        Add(parameters, "candidate_finding_count", request.CandidateFindingCount);
        AddJsonb(parameters, "candidate_findings_json", request.CandidateFindingsJson);
        AddJsonb(parameters, "reviewer_provenance_json", request.ReviewerProvenanceJson);
        AddBigint(parameters, "reviewer_input_tokens", request.InputTokens);
        AddBigint(parameters, "reviewer_output_tokens", request.OutputTokens);
        AddNumeric(parameters, "estimated_cost_usd", request.EstimatedCostUsd);

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Reviewed',
                outcome = 'Unlabelled',
                candidate_storage_uri = @candidate_storage_uri,
                candidate_sha256 = @candidate_sha256,
                candidate_byte_count = @candidate_byte_count,
                candidate_finding_count = @candidate_finding_count,
                candidate_findings_json = @candidate_findings_json,
                reviewer_provenance_json = @reviewer_provenance_json,
                reviewer_input_tokens = @reviewer_input_tokens,
                reviewer_output_tokens = @reviewer_output_tokens,
                estimated_cost_usd = @estimated_cost_usd,
                completed_at_utc = @now,
                updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Reviewing'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: false)}
            """,
            parameters,
            CodeReviewEvaluationCaseOutcome.Unlabelled,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationCaseResult> StartGradingAsync(
        CodeReviewEvaluationCaseGrading request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        if (request.ReusedCandidate is null)
        {
            return await TransitionAsync(
                request.OrganizationId,
                request.RunId,
                request.CaseId,
                $"""
                UPDATE code_review_evaluation_case_results AS c
                SET phase = 'Grading', updated_at_utc = @now
                WHERE c.organization_id = @org
                  AND c.run_id = @run
                  AND c.manifest_case_id = @case
                  AND c.phase = 'Reviewed'
                  AND c.outcome = 'None'
                  AND c.disabled_at_utc IS NULL
                  AND {LiveLeaseGuard(requireNoCancelRequest: true)}
                """,
                BaseParameters(
                    request.OrganizationId,
                    request.RunId,
                    request.CaseId,
                    request.LeaseToken,
                    now
                ),
                null,
                cancellationToken
            );
        }

        var parentRunId = await ValidateParentCandidateAsync(request, cancellationToken);

        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        AddArtifactParameters(parameters, request.ReusedCandidate);
        Add(parameters, "parent_run", parentRunId);

        // Judge-only retry from a pending child: copy the parent's whole candidate record, not
        // just its identity, so the retry report keeps findings, count, provenance and usage.
        // Every artifact identity field must match the immutable parent row, and reused review
        // cost is carried over rather than charged again.
        // NOTE: values are bound as Npgsql parameters; the only interpolation is the constant
        // lease-guard fragment, so EF1002 does not apply here.
#pragma warning disable EF1002
        var reusedFromPending = await db.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Grading',
                candidate_storage_uri = p.candidate_storage_uri,
                candidate_sha256 = p.candidate_sha256,
                candidate_byte_count = p.candidate_byte_count,
                candidate_finding_count = p.candidate_finding_count,
                candidate_findings_json = p.candidate_findings_json,
                reviewer_provenance_json = p.reviewer_provenance_json,
                reviewer_input_tokens = p.reviewer_input_tokens,
                reviewer_output_tokens = p.reviewer_output_tokens,
                estimated_cost_usd = p.estimated_cost_usd,
                updated_at_utc = @now
            FROM code_review_evaluation_case_results AS p
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Pending'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND p.organization_id = @org
              AND p.run_id = @parent_run
              AND p.manifest_case_id = @case
              AND p.outcome = 'JudgeFailed'
              AND p.candidate_storage_uri = @candidate_storage_uri
              AND p.candidate_sha256 = @candidate_sha256
              AND p.candidate_byte_count = @candidate_byte_count
              AND {LiveLeaseGuard(requireNoCancelRequest: true)}
            """,
            parameters,
            cancellationToken
        );
#pragma warning restore EF1002

        if (reusedFromPending > 0)
        {
            return await ResolveAsync(
                request.OrganizationId,
                request.RunId,
                request.CaseId,
                request.LeaseToken,
                reusedFromPending,
                null,
                cancellationToken
            );
        }

        // A child that already owns a candidate keeps its own record; only the artifact
        // identity is cross-checked against the requested parent artifact.
        return await TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Grading', updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Reviewed'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND c.candidate_storage_uri = @candidate_storage_uri
              AND c.candidate_sha256 = @candidate_sha256
              AND c.candidate_byte_count = @candidate_byte_count
              AND {LiveLeaseGuard(requireNoCancelRequest: true)}
            """,
            parameters,
            null,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordGradeAsync(
        CodeReviewEvaluationCaseGrade request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();
        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        Add(parameters, "matched_reference_count", request.MatchedReferenceCount);
        Add(parameters, "missed_reference_count", request.MissedReferenceCount);
        Add(parameters, "expected_reference_count", request.ExpectedReferenceCount);
        Add(parameters, "additional_finding_count", request.AdditionalFindingCount);
        AddJsonb(parameters, "judge_matches_json", request.JudgeMatchesJson);
        AddJsonb(parameters, "judge_reasons_json", request.JudgeReasonsJson);
        AddJsonb(parameters, "judge_provenance_json", request.JudgeProvenanceJson);
        AddBigint(parameters, "judge_input_tokens", request.InputTokens);
        AddBigint(parameters, "judge_output_tokens", request.OutputTokens);
        AddNumeric(parameters, "estimated_cost_usd", request.EstimatedCostUsd);

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET phase = 'Graded',
                outcome = 'Graded',
                matched_reference_count = @matched_reference_count,
                missed_reference_count = @missed_reference_count,
                expected_reference_count = @expected_reference_count,
                additional_finding_count = @additional_finding_count,
                judge_matches_json = @judge_matches_json,
                judge_reasons_json = @judge_reasons_json,
                judge_provenance_json = @judge_provenance_json,
                judge_input_tokens = @judge_input_tokens,
                judge_output_tokens = @judge_output_tokens,
                estimated_cost_usd = CASE
                    WHEN estimated_cost_usd IS NULL AND @estimated_cost_usd IS NULL THEN NULL
                    ELSE COALESCE(estimated_cost_usd, 0) + COALESCE(@estimated_cost_usd, 0)
                END,
                completed_at_utc = @now,
                updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase = 'Grading'
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: false)}
            """,
            parameters,
            CodeReviewEvaluationCaseOutcome.Graded,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordFailureAsync(
        CodeReviewEvaluationCaseFailure request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.Outcome
            is not (
                CodeReviewEvaluationCaseOutcome.ReviewFailed
                or CodeReviewEvaluationCaseOutcome.JudgeFailed
            )
        )
        {
            throw new ArgumentException(
                "Failure outcomes are limited to ReviewFailed and JudgeFailed.",
                nameof(request)
            );
        }

        var now = timeProvider.GetUtcNow();
        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        Add(parameters, "outcome", request.Outcome.ToString());
        Add(parameters, "error_code", request.ErrorCode);
        Add(parameters, "failure_message", request.FailureMessage);

        // A reviewer failure happens before any candidate exists. A judge failure requires a
        // persisted candidate, and is allowed from `Reviewed` as well as `Grading`: the transition
        // into grading can fail transiently after the candidate was written, and that failure
        // must still be recordable or judge-only retry loses the case.
        var outcomeGuard =
            request.Outcome == CodeReviewEvaluationCaseOutcome.ReviewFailed
                ? "AND c.candidate_storage_uri IS NULL AND c.phase IN ('Pending', 'Reviewing')"
                : "AND c.phase IN ('Reviewed', 'Grading') AND c.candidate_storage_uri IS NOT NULL";

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET outcome = @outcome,
                error_code = @error_code,
                failure_message = @failure_message,
                completed_at_utc = @now,
                updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              {outcomeGuard}
              AND {LiveLeaseGuard(requireNoCancelRequest: false)}
            """,
            parameters,
            request.Outcome,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordSkipAsync(
        CodeReviewEvaluationCaseSkip request,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();
        var parameters = BaseParameters(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            request.LeaseToken,
            now
        );
        Add(parameters, "skip_reason", request.SkipReason);

        return TransitionAsync(
            request.OrganizationId,
            request.RunId,
            request.CaseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET outcome = 'Skipped',
                error_code = @skip_reason,
                completed_at_utc = @now,
                updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.phase IN ('Pending', 'Reviewing')
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: true)}
            """,
            parameters,
            CodeReviewEvaluationCaseOutcome.Skipped,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult> RecordCancelledAsync(
        string organizationId,
        string runId,
        string caseId,
        string leaseToken,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        return TransitionAsync(
            organizationId,
            runId,
            caseId,
            $"""
            UPDATE code_review_evaluation_case_results AS c
            SET outcome = 'Cancelled', completed_at_utc = @now, updated_at_utc = @now
            WHERE c.organization_id = @org
              AND c.run_id = @run
              AND c.manifest_case_id = @case
              AND c.outcome = 'None'
              AND c.disabled_at_utc IS NULL
              AND {LiveLeaseGuard(requireNoCancelRequest: false)}
            """,
            BaseParameters(organizationId, runId, caseId, leaseToken, now),
            CodeReviewEvaluationCaseOutcome.Cancelled,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewEvaluationCaseResult>> ListForRunAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_case_result.list_for_run"
            )
            .AsNoTracking()
            .Where(result =>
                result.OrganizationId == organizationId
                && result.RunId == runId
                && result.DisabledAtUtc == null
            )
            .OrderBy(result => result.PullRequestNumber)
            .ThenBy(result => result.ManifestCaseId)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewEvaluationCaseResult?> FindAsync(
        string organizationId,
        string runId,
        string caseId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_case_result.find"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                result =>
                    result.OrganizationId == organizationId
                    && result.RunId == runId
                    && result.ManifestCaseId == caseId
                    && result.DisabledAtUtc == null,
                cancellationToken
            );

    private static string LiveLeaseGuard(bool requireNoCancelRequest) =>
        $"""
            EXISTS (
                SELECT 1 FROM code_review_evaluation_runs AS r
                WHERE r.organization_id = @org
                  AND r.id = @run
                  AND r.state = 'Running'
                  AND r.lease_token = @token
                  AND r.lease_expires_at_utc > now()
                  {(
                requireNoCancelRequest ? "AND r.cancel_requested_at_utc IS NULL" : string.Empty
            )})
            """;

    private static List<NpgsqlParameter> BaseParameters(
        string organizationId,
        string runId,
        string caseId,
        string leaseToken,
        DateTimeOffset now
    ) =>
        [
            new("org", organizationId),
            new("run", runId),
            new("case", caseId),
            new("token", leaseToken),
            new("now", now),
        ];

    private static void AddArtifactParameters(
        List<NpgsqlParameter> parameters,
        CodeReviewEvaluationArtifact artifact
    )
    {
        Add(parameters, "candidate_storage_uri", artifact.StorageUri);
        Add(parameters, "candidate_sha256", artifact.Sha256);
        Add(parameters, "candidate_byte_count", artifact.ByteCount);
    }

    private static void Add(List<NpgsqlParameter> parameters, string name, object? value) =>
        parameters.Add(new(name, value ?? DBNull.Value));

    /// <summary>
    /// Binds a jsonb parameter explicitly.
    /// </summary>
    /// <remarks>
    /// A parameter inferred as text cannot be assigned to a jsonb column; PostgreSQL rejects it
    /// with 42804. Every JSON payload column goes through this helper.
    /// </remarks>
    private static void AddJsonb(List<NpgsqlParameter> parameters, string name, string? value) =>
        parameters.Add(new(name, NpgsqlDbType.Jsonb) { Value = (object?)value ?? DBNull.Value });

    /// <summary>
    /// Binds a nullable numeric explicitly.
    /// </summary>
    /// <remarks>
    /// A null parameter with no type inference cannot be used in `IS NULL` or `COALESCE`
    /// expressions; PostgreSQL rejects it with 42P18. Passing DBNull through the generic helper
    /// produced exactly that when a judge reported no cost, which failed every real grade.
    /// </remarks>
    private static void AddNumeric(List<NpgsqlParameter> parameters, string name, decimal? value) =>
        parameters.Add(new(name, NpgsqlDbType.Numeric) { Value = (object?)value ?? DBNull.Value });

    /// <summary>Binds a nullable bigint explicitly for the same reason as numeric.</summary>
    private static void AddBigint(List<NpgsqlParameter> parameters, string name, long? value) =>
        parameters.Add(new(name, NpgsqlDbType.Bigint) { Value = (object?)value ?? DBNull.Value });

    private async Task<CodeReviewEvaluationCaseResult> TransitionAsync(
        string organizationId,
        string runId,
        string caseId,
        string sql,
        List<NpgsqlParameter> parameters,
        CodeReviewEvaluationCaseOutcome? expectedTerminalOutcome,
        CancellationToken cancellationToken
    )
    {
        var affected = await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

        // Every case transition binds its lease token as the "token" parameter.
        var leaseToken =
            parameters.FirstOrDefault(parameter => parameter.ParameterName == "token")?.Value
                as string
            ?? string.Empty;

        return await ResolveAsync(
            organizationId,
            runId,
            caseId,
            leaseToken,
            affected,
            expectedTerminalOutcome,
            cancellationToken
        );
    }

    private async Task<string> ValidateParentCandidateAsync(
        CodeReviewEvaluationCaseGrading request,
        CancellationToken cancellationToken
    )
    {
        var run = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_case_result.retry_parent_run"
            )
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == request.OrganizationId
                    && candidate.Id == request.RunId,
                cancellationToken
            );

        if (run?.ParentRunId is null)
        {
            throw new CodeReviewEvaluationFencedException(
                "parent_missing",
                "A judge-only retry requires a parent run."
            );
        }

        var parentResult = await db
            .CodeReviewEvaluationCaseResults.TagWithOperationCallSite(
                "code_review_evaluation_case_result.retry_parent_case"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                result =>
                    result.OrganizationId == request.OrganizationId
                    && result.RunId == run.ParentRunId
                    && result.ManifestCaseId == request.CaseId,
                cancellationToken
            );

        if (parentResult is null)
        {
            throw new CodeReviewEvaluationFencedException(
                "parent_case_missing",
                "The parent run has no result for this manifest case."
            );
        }

        if (
            parentResult.Outcome != CodeReviewEvaluationCaseOutcome.JudgeFailed
            || parentResult.CandidateStorageUri is null
            || !string.Equals(
                parentResult.CandidateStorageUri,
                request.ReusedCandidate!.StorageUri,
                StringComparison.Ordinal
            )
            || !string.Equals(
                parentResult.CandidateSha256,
                request.ReusedCandidate.Sha256,
                StringComparison.Ordinal
            )
            || parentResult.CandidateByteCount != request.ReusedCandidate.ByteCount
        )
        {
            throw new CodeReviewEvaluationFencedException(
                "parent_candidate_mismatch",
                "The reused candidate artifact does not match the parent judge-failed result."
            );
        }

        return run.ParentRunId;
    }

    private async Task<CodeReviewEvaluationCaseResult> ResolveAsync(
        string organizationId,
        string runId,
        string caseId,
        string leaseToken,
        int affected,
        CodeReviewEvaluationCaseOutcome? expectedTerminalOutcome,
        CancellationToken cancellationToken
    )
    {
        var result = await FindAsync(organizationId, runId, caseId, cancellationToken)
            ?? throw new CodeReviewEvaluationFencedException(
                "case_not_found",
                "The manifest case result does not exist for this run."
            );

        var run = await db
            .CodeReviewEvaluationRuns.TagWithOperationCallSite(
                "code_review_evaluation_case_result.diagnose"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId && candidate.Id == runId,
                cancellationToken
            );

        if (run is null)
        {
            throw new CodeReviewEvaluationFencedException(
                "run_not_found",
                "The evaluation run does not exist for this organization."
            );
        }

        var leaseLive =
            run.State == CodeReviewEvaluationRunState.Running
            && string.Equals(run.LeaseToken, leaseToken, StringComparison.Ordinal)
            && run.LeaseExpiresAtUtc is { } expiry
            && expiry > timeProvider.GetUtcNow();

        if (affected > 0)
        {
            return result;
        }

        if (!leaseLive)
        {
            // Ownership must be proven before a terminal outcome can be reported as an
            // idempotent success. Otherwise a stale writer would see "already done" for a
            // transition it never applied. A duplicate delivery after the run itself finished
            // is still acknowledged, because the terminal row is durable and immutable.
            if (
                result.Outcome != CodeReviewEvaluationCaseOutcome.None
                && IsTerminal(run.State)
                && result.Outcome == expectedTerminalOutcome
            )
            {
                return result;
            }

            throw new CodeReviewEvaluationFencedException(
                run.State != CodeReviewEvaluationRunState.Running
                    ? "run_not_running"
                    : "lease_expired",
                "The case write lost ownership of the evaluation run."
            );
        }

        // Terminal outcomes are immutable, so a live owner repeating the exact transition is a
        // no-op rather than an error. A different terminal outcome is a mismatch, not a success.
        if (result.Outcome != CodeReviewEvaluationCaseOutcome.None)
        {
            if (result.Outcome == expectedTerminalOutcome)
            {
                return result;
            }

            throw new CodeReviewEvaluationFencedException(
                "terminal_outcome_mismatch",
                $"The case already ended as {result.Outcome}, not {expectedTerminalOutcome}."
            );
        }

        if (run.CancelRequestedAtUtc is not null)
        {
            throw new CodeReviewEvaluationFencedException(
                "cancel_requested",
                "The evaluation run has a pending cancellation request."
            );
        }

        throw new CodeReviewEvaluationFencedException(
            "invalid_phase",
            $"The case is in phase {result.Phase} and cannot make the requested transition."
        );
    }

    private static bool IsTerminal(CodeReviewEvaluationRunState state) =>
        state
            is CodeReviewEvaluationRunState.Completed
                or CodeReviewEvaluationRunState.Incomplete
                or CodeReviewEvaluationRunState.Cancelled
                or CodeReviewEvaluationRunState.Abandoned
                or CodeReviewEvaluationRunState.DispatchFailed;
}
