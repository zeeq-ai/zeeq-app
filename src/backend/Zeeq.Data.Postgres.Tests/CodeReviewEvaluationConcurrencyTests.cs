using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Concurrency and retry-copy proofs for evaluation persistence.
/// </summary>
/// <remarks>
/// The transactional fixture cannot prove concurrent behavior: one outer transaction hides a
/// competitor's committed writes. These tests use separate contexts against committed,
/// test-owned rows and delete them in a finally block. Aggregate-key cleanup is exact because
/// every seed creates its own organization id.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo
/// </remarks>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewEvaluationConcurrencyTests(PgDatabaseFixture postgres)
{
    private const string OwnerUserId = "usr_concurrency_owner";

    [Test]
    public async Task SameVersionSave_TwoContexts_ExactlyOneWins()
    {
        var seed = await SeedAsync();
        try
        {
            await using var first = postgres.CreateContext();
            await using var second = postgres.CreateContext();

            var firstStore = new PostgresCodeReviewEvaluationDatasetStore(
                first,
                TimeProvider.System
            );
            var secondStore = new PostgresCodeReviewEvaluationDatasetStore(
                second,
                TimeProvider.System
            );

            // Both handlers preload the same version before either save runs.
            var firstLoaded = await firstStore.FindAsync(
                seed.OrganizationId,
                seed.DatasetId,
                false,
                CancellationToken.None
            );
            var secondLoaded = await secondStore.FindAsync(
                seed.OrganizationId,
                seed.DatasetId,
                false,
                CancellationToken.None
            );
            await Assert.That(firstLoaded!.Version).IsEqualTo(secondLoaded!.Version);

            var results = await Task.WhenAll(
                firstStore.SaveAsync(
                    NewSave(seed, firstLoaded.Version, 902, "sha-first"),
                    CancellationToken.None
                ),
                secondStore.SaveAsync(
                    NewSave(seed, secondLoaded.Version, 903, "sha-second"),
                    CancellationToken.None
                )
            );

            await Assert
                .That(
                    results.Count(result =>
                        result.Outcome == CodeReviewEvaluationDatasetSaveOutcome.Saved
                    )
                )
                .IsEqualTo(1);
            await Assert
                .That(
                    results.Count(result =>
                        result.Outcome == CodeReviewEvaluationDatasetSaveOutcome.VersionConflict
                    )
                )
                .IsEqualTo(1);

            await using var verification = postgres.CreateContext();
            var dataset = await verification
                .CodeReviewEvaluationDatasets.AsNoTracking()
                .SingleAsync(item => item.Id == seed.DatasetId);

            // One version increment, and only the winner's membership was installed: the
            // pre-existing case plus the winner's new case.
            await Assert.That(dataset.Version).IsEqualTo(3);
            await Assert
                .That(
                    await verification
                        .CodeReviewEvaluationCases.AsNoTracking()
                        .CountAsync(item =>
                            item.DatasetId == seed.DatasetId && item.RemovedAtUtc == null
                        )
                )
                .IsEqualTo(2);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Test]
    public async Task ParallelAdmissions_SameOrganization_ExactlyOneAdmitted()
    {
        var seed = await SeedAsync();
        try
        {
            await using var first = postgres.CreateContext();
            await using var second = postgres.CreateContext();
            var firstStore = new PostgresCodeReviewEvaluationRunStore(first, TimeProvider.System);
            var secondStore = new PostgresCodeReviewEvaluationRunStore(second, TimeProvider.System);

            var results = await Task.WhenAll(
                firstStore.AdmitAsync(
                    NewAdmission(seed, "concurrent-request-a"),
                    CancellationToken.None
                ),
                secondStore.AdmitAsync(
                    NewAdmission(seed, "concurrent-request-b"),
                    CancellationToken.None
                )
            );

            await Assert
                .That(
                    results.Count(result =>
                        result.Outcome == CodeReviewEvaluationRunAdmissionOutcome.Admitted
                    )
                )
                .IsEqualTo(1);
            await Assert
                .That(
                    results.Count(result =>
                        result.Outcome == CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict
                    )
                )
                .IsEqualTo(1);

            var admitted = results.Single(result =>
                result.Outcome == CodeReviewEvaluationRunAdmissionOutcome.Admitted
            );
            var conflict = results.Single(result =>
                result.Outcome == CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict
            );

            await Assert.That(conflict.ActiveRunId).IsEqualTo(admitted.Run!.Id);

            await using var verification = postgres.CreateContext();
            await Assert
                .That(
                    await verification
                        .CodeReviewEvaluationRuns.AsNoTracking()
                        .CountAsync(run =>
                            run.OrganizationId == seed.OrganizationId
                            && (
                                run.State == CodeReviewEvaluationRunState.Queued
                                || run.State == CodeReviewEvaluationRunState.Running
                            )
                        )
                )
                .IsEqualTo(1);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Test]
    public async Task QueuedCancelRacingClaim_LeavesOneLegalState()
    {
        var seed = await SeedAsync();
        try
        {
            await using var admission = postgres.CreateContext();
            var admitted = await new PostgresCodeReviewEvaluationRunStore(
                admission,
                TimeProvider.System
            ).AdmitAsync(NewAdmission(seed, "cancel-race-request"), CancellationToken.None);
            var runId = admitted.Run!.Id;

            await using var cancelContext = postgres.CreateContext();
            await using var claimContext = postgres.CreateContext();
            var cancelStore = new PostgresCodeReviewEvaluationRunStore(
                cancelContext,
                TimeProvider.System
            );
            var claimStore = new PostgresCodeReviewEvaluationRunStore(
                claimContext,
                TimeProvider.System
            );

            var cancelTask = cancelStore.RequestCancelAsync(
                seed.OrganizationId,
                runId,
                CancellationToken.None
            );
            var claimTask = claimStore.TryClaimAsync(
                seed.OrganizationId,
                runId,
                "lease-race",
                TimeSpan.FromMinutes(5),
                CancellationToken.None
            );

            await Task.WhenAll(cancelTask, claimTask);
            var claim = await claimTask;

            await using var verification = postgres.CreateContext();
            var run = await verification
                .CodeReviewEvaluationRuns.AsNoTracking()
                .SingleAsync(item => item.Id == runId);

            if (run.State == CodeReviewEvaluationRunState.Cancelled)
            {
                // Cancel won the queued race: no consumer may own it.
                await Assert
                    .That(claim.Outcome)
                    .IsEqualTo(CodeReviewEvaluationRunClaimOutcome.NotRunnable);
                await Assert.That(run.LeaseToken).IsNull();
            }
            else
            {
                // Claim won: the run is running and carries the cancellation request, so new
                // cases stop but finished results survive.
                await Assert.That(run.State).IsEqualTo(CodeReviewEvaluationRunState.Running);
                await Assert.That(run.CancelRequestedAtUtc).IsNotNull();
                await Assert
                    .That(claim.Outcome)
                    .IsEqualTo(CodeReviewEvaluationRunClaimOutcome.Claimed);
            }

            await Assert.That(run.State).IsNotEqualTo(CodeReviewEvaluationRunState.Queued);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Test]
    public async Task RetryChild_CopiesJudgeFailedParentFieldsWithoutMutatingParent()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = postgres.CreateContext();
            var runStore = new PostgresCodeReviewEvaluationRunStore(db, TimeProvider.System);
            var resultStore = new PostgresCodeReviewEvaluationCaseResultStore(
                db,
                TimeProvider.System
            );

            var parent = (
                await runStore.AdmitAsync(
                    NewAdmission(seed, "retry-parent-request"),
                    CancellationToken.None
                )
            ).Run!;
            var caseId = seed.CaseId;

            await runStore.TryClaimAsync(
                seed.OrganizationId,
                parent.Id,
                "lease-parent",
                TimeSpan.FromMinutes(10),
                CancellationToken.None
            );
            await resultStore.StartReviewAsync(
                new(seed.OrganizationId, parent.Id, caseId, "lease-parent"),
                CancellationToken.None
            );

            var candidateArtifact = new CodeReviewEvaluationArtifact(
                "postgres://reviewer-evaluations/org/repo/retry/output.xml",
                new string('a', 64),
                512
            );
            await resultStore.RecordCandidateAsync(
                new(
                    seed.OrganizationId,
                    parent.Id,
                    caseId,
                    "lease-parent",
                    candidateArtifact,
                    CandidateFindingCount: 2,
                    CandidateFindingsJson: "[{\"id\":\"cand_1\"},{\"id\":\"cand_2\"}]",
                    ReviewerProvenanceJson: "{\"model\":\"fixture-reviewer\"}",
                    InputTokens: 111,
                    OutputTokens: 22,
                    EstimatedCostUsd: 0.05m
                ),
                CancellationToken.None
            );
            await resultStore.StartGradingAsync(
                new(seed.OrganizationId, parent.Id, caseId, "lease-parent", null),
                CancellationToken.None
            );
            await resultStore.RecordFailureAsync(
                new(
                    seed.OrganizationId,
                    parent.Id,
                    caseId,
                    "lease-parent",
                    CodeReviewEvaluationCaseOutcome.JudgeFailed,
                    "JudgeInvalidJson",
                    "Fixture judge returned invalid JSON"
                ),
                CancellationToken.None
            );
            await runStore.FinalizeAsync(
                new(
                    seed.OrganizationId,
                    parent.Id,
                    "lease-parent",
                    CodeReviewEvaluationRunState.Incomplete,
                    null,
                    null
                ),
                CancellationToken.None
            );

            var parentBefore = await LoadResultAsync(db, seed.OrganizationId, parent.Id, caseId);

            var retry = await runStore.AdmitRetryAsync(
                new(
                    seed.OrganizationId,
                    parent.Id,
                    "retry-child-request",
                    OwnerUserId,
                    "build-test",
                    "prompt-v1",
                    "fingerprint-retry-child"
                ),
                CancellationToken.None
            );
            await Assert
                .That(retry.Outcome)
                .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.Admitted);
            var childId = retry.Run!.Id;

            await runStore.TryClaimAsync(
                seed.OrganizationId,
                childId,
                "lease-child",
                TimeSpan.FromMinutes(10),
                CancellationToken.None
            );
            await resultStore.StartGradingAsync(
                new(seed.OrganizationId, childId, caseId, "lease-child", candidateArtifact),
                CancellationToken.None
            );

            var child = await LoadResultAsync(db, seed.OrganizationId, childId, caseId);
            await Assert.That(child.Phase).IsEqualTo(CodeReviewEvaluationCasePhase.Grading);
            await Assert.That(child.CandidateStorageUri).IsEqualTo(candidateArtifact.StorageUri);
            await Assert.That(child.CandidateSha256).IsEqualTo(candidateArtifact.Sha256);
            await Assert.That(child.CandidateByteCount).IsEqualTo(candidateArtifact.ByteCount);
            await Assert.That(child.CandidateFindingCount).IsEqualTo(2);
            // jsonb normalizes whitespace, so fidelity is proven against the parent's own
            // persisted form rather than a hand-written JSON literal.
            await Assert
                .That(child.CandidateFindingsJson)
                .IsEqualTo(parentBefore.CandidateFindingsJson);
            await Assert
                .That(child.ReviewerProvenanceJson)
                .IsEqualTo(parentBefore.ReviewerProvenanceJson);
            await Assert.That(child.ReviewerInputTokens).IsEqualTo(111);
            await Assert.That(child.ReviewerOutputTokens).IsEqualTo(22);
            await Assert.That(child.EstimatedCostUsd).IsEqualTo(0.05m);

            var parentAfter = await LoadResultAsync(db, seed.OrganizationId, parent.Id, caseId);
            await Assert
                .That(parentAfter.Outcome)
                .IsEqualTo(CodeReviewEvaluationCaseOutcome.JudgeFailed);
            await Assert
                .That(parentAfter.CandidateFindingsJson)
                .IsEqualTo(parentBefore.CandidateFindingsJson);
            await Assert.That(parentAfter.CandidateSha256).IsEqualTo(parentBefore.CandidateSha256);
            await Assert
                .That(parentAfter.ReviewerInputTokens)
                .IsEqualTo(parentBefore.ReviewerInputTokens);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    private async Task<ConcurrencySeed> SeedAsync()
    {
        await using var db = postgres.CreateContext();
        await TestBiome.Start(db, out var seed).AddRepository(out var repository).BuildAsync();

        var agent = new CodeReviewerAgent
        {
            Id = SeedContext.NewId("cra"),
            OrganizationId = repository.OrganizationId,
            RepositoryId = repository.Id,
            DisplayName = "Concurrency fixture reviewer",
            ReviewFacet = "Correctness",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review for correctness.",
            Enabled = true,
            ActivationConfiguration = new CodeReviewerActivationConfiguration(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var datasetStore = new PostgresCodeReviewEvaluationDatasetStore(db, TimeProvider.System);
        var dataset = await datasetStore.CreateAsync(
            new CodeReviewEvaluationDataset
            {
                Id = SeedContext.NewId("cred"),
                OrganizationId = repository.OrganizationId,
                RepositoryId = repository.Id,
                Name = "Concurrency dataset",
                Version = 1,
                CreateRequestKey = SeedContext.NewId("key"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        // Insert the agent after the dataset so the repository exists; then add one case.
        db.CodeReviewerAgents.Add(agent);
        await db.SaveChangesAsync();

        var saved = await datasetStore.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                1,
                dataset.Name,
                null,
                [
                    new(
                        null,
                        901,
                        new CodeReviewEvaluationStagedCapture(
                            new CodeReviewEvaluationArtifact(
                                "postgres://reviewer-evaluations/org/repo/seed/snapshot.json",
                                new string('b', 64),
                                256
                            ),
                            901,
                            "Concurrency case",
                            "owner@example.test",
                            "base",
                            "head",
                            PullRequestState.Open,
                            DateTimeOffset.UtcNow,
                            1,
                            0,
                            1,
                            null
                        ),
                        null
                    ),
                ]
            ),
            CancellationToken.None
        );

        return new(
            seed.Organization.Id,
            repository.Id,
            agent.Id,
            dataset.Id,
            saved.Cases!.Single().Id,
            saved.Dataset!.Version,
            seed
        );
    }

    private static CodeReviewEvaluationDatasetSaveRequest NewSave(
        ConcurrencySeed seed,
        long expectedVersion,
        int pullRequestNumber,
        string shaSeed
    ) =>
        new(
            seed.OrganizationId,
            seed.RepositoryId,
            seed.DatasetId,
            expectedVersion,
            "Concurrency dataset",
            null,
            [
                new(seed.CaseId, seed.PullRequestNumber, null, null),
                new(
                    null,
                    pullRequestNumber,
                    new CodeReviewEvaluationStagedCapture(
                        new CodeReviewEvaluationArtifact(
                            $"postgres://reviewer-evaluations/org/repo/{shaSeed}/snapshot.json",
                            new string('c', 64),
                            128
                        ),
                        pullRequestNumber,
                        $"Concurrency {pullRequestNumber}",
                        "owner@example.test",
                        "base",
                        $"head-{shaSeed}",
                        PullRequestState.Open,
                        DateTimeOffset.UtcNow,
                        1,
                        0,
                        1,
                        null
                    ),
                    null
                ),
            ]
        );

    private static CodeReviewEvaluationRunAdmission NewAdmission(
        ConcurrencySeed seed,
        string requestKey
    ) =>
        new(
            seed.OrganizationId,
            seed.RepositoryId,
            seed.DatasetId,
            seed.DatasetVersion,
            seed.AgentId,
            null,
            new CodeReviewerAgentRevisionConfiguration(
                "Concurrency fixture reviewer",
                "Correctness",
                CodeReviewModelTier.High,
                "Review for correctness.",
                true,
                new CodeReviewerActivationConfiguration()
            ),
            new(
                CodeReviewModelTier.High,
                "openai",
                "gpt-test",
                null,
                CodeReviewEvaluationCredentialSource.SystemDefault,
                null
            ),
            new(
                CodeReviewModelTier.Fast,
                "openai",
                "gpt-test-judge",
                null,
                CodeReviewEvaluationCredentialSource.SystemDefault,
                null
            ),
            "judge-rubric-v1",
            "shared system prompt",
            new string('d', 64),
            "{}",
            [],
            "build-test",
            "prompt-v1",
            OwnerUserId,
            requestKey,
            $"fingerprint-{requestKey}"
        );

    private static async Task<CodeReviewEvaluationCaseResult> LoadResultAsync(
        PostgresDbContext db,
        string organizationId,
        string runId,
        string caseId
    )
    {
        db.ChangeTracker.Clear();

        return await db
            .CodeReviewEvaluationCaseResults.AsNoTracking()
            .SingleAsync(result =>
                result.OrganizationId == organizationId
                && result.RunId == runId
                && result.ManifestCaseId == caseId
            );
    }

    private async Task CleanupAsync(ConcurrencySeed seed)
    {
        var organizationId = seed.OrganizationId;
        await using var db = postgres.CreateContext();

        // The organization id is unique to this test, so organization-scoped deletes are exact.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_case_results WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_runs WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_cases WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_datasets WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_reviewer_agent_revisions WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_reviewer_agents WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_repositories WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.core_team_memberships WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.core_organization_memberships WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.core_teams WHERE organization_id = {organizationId}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.core_organizations WHERE id = {organizationId}"
        );

        // Exact seed users only; never pattern-delete rows that other tests own.
        foreach (var user in seed.Seed.Users)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM zeeq.core_users WHERE id = {user.Id}"
            );
        }
    }

    private sealed record ConcurrencySeed(
        string OrganizationId,
        string RepositoryId,
        string AgentId,
        string DatasetId,
        string CaseId,
        long DatasetVersion,
        SeedContext Seed
    )
    {
        public int PullRequestNumber => 901;
    }
}
