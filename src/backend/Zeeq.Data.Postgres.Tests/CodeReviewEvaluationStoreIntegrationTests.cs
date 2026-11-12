using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for evaluation dataset versions, capture identity and run ownership.
/// </summary>
/// <remarks>
/// These are the invariants that silently corrupt history when they break: compare-and-swap
/// saves, stable case identity across remove/re-add, capture resets, frozen manifests and
/// fenced writes. Assertions use fresh reads so a change-tracker artifact cannot pass a test.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo
/// </remarks>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewEvaluationStoreIntegrationTests : PgTransactionalTestBase
{
    private readonly PgDatabaseFixture _postgres;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public CodeReviewEvaluationStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) => _postgres = postgres;

    private PostgresCodeReviewEvaluationDatasetStore Datasets => new(_context, _clock);

    private PostgresCodeReviewEvaluationRunStore Runs => new(_context, _clock);

    private PostgresCodeReviewEvaluationCaseResultStore Results => new(_context, _clock);

    private PostgresCodeReviewEvaluationArtifactStore Artifacts =>
        new(new PostgresStorageProvider(_context));

    [Test]
    public async Task CreateAsync_IsIdempotentOnRequestKey()
    {
        var (repository, _) = await SeedRepositoryAsync();
        var store = Datasets;

        var first = await store.CreateAsync(
            NewDataset(repository, "create-key-1"),
            CancellationToken.None
        );
        var second = await store.CreateAsync(
            NewDataset(repository, "create-key-1"),
            CancellationToken.None
        );

        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert
            .That(
                await _context
                    .CodeReviewEvaluationDatasets.Where(dataset =>
                        dataset.OrganizationId == repository.OrganizationId
                        && dataset.CreateRequestKey == "create-key-1"
                    )
                    .CountAsync()
            )
            .IsEqualTo(1);
    }

    [Test]
    public async Task SaveAsync_RejectsStaleVersionWithoutWriting()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "cas-key"),
            CancellationToken.None
        );

        var firstSave = await store.SaveAsync(
            NewSaveRequest(dataset, [NewCase(901, agent.Id, "sha-1")]),
            CancellationToken.None
        );
        await Assert
            .That(firstSave.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);

        var staleSave = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: 1,
                "Renamed",
                null,
                [NewCase(902, agent.Id, "sha-2")]
            ),
            CancellationToken.None
        );

        await Assert
            .That(staleSave.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.VersionConflict);

        _context.ChangeTracker.Clear();
        var reloaded = await _context
            .CodeReviewEvaluationDatasets.Where(item =>
                item.OrganizationId == dataset.OrganizationId && item.Id == dataset.Id
            )
            .SingleAsync();

        await Assert.That(reloaded.Version).IsEqualTo(2);
        await Assert.That(reloaded.Name).IsEqualTo(dataset.Name);
        await Assert
            .That(
                await _context
                    .CodeReviewEvaluationCases.Where(item =>
                        item.OrganizationId == dataset.OrganizationId
                        && item.DatasetId == dataset.Id
                    )
                    .CountAsync()
            )
            .IsEqualTo(1);
    }

    [Test]
    public async Task SaveAsync_RemoveThenReAdd_KeepsCaseIdAndLeavesCaseActive()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "readd-key"),
            CancellationToken.None
        );

        var added = await store.SaveAsync(
            NewSaveRequest(dataset, [NewCase(901, agent.Id, "sha-a")]),
            CancellationToken.None
        );
        var caseId = added.Cases!.Single().Id;

        var removed = await store.SaveAsync(
            new(dataset.OrganizationId, repository.Id, dataset.Id, 2, dataset.Name, null, []),
            CancellationToken.None
        );
        await Assert.That(removed.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        await Assert.That(removed.Cases!.Count).IsEqualTo(0);

        var reAdded = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: 3,
                dataset.Name,
                null,
                [NewCase(901, agent.Id, "sha-b")]
            ),
            CancellationToken.None
        );

        await Assert.That(reAdded.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        var reAddedCase = reAdded.Cases!.Single();

        // Stable identity AND active membership. A desired-set bug once left the re-added row
        // soft-removed in the same save.
        await Assert.That(reAddedCase.Id).IsEqualTo(caseId);
        await Assert.That(reAddedCase.RemovedAtUtc).IsNull();

        _context.ChangeTracker.Clear();
        await Assert
            .That(
                await _context
                    .CodeReviewEvaluationCases.Where(item =>
                        item.OrganizationId == dataset.OrganizationId
                        && item.DatasetId == dataset.Id
                        && item.RemovedAtUtc == null
                    )
                    .CountAsync()
            )
            .IsEqualTo(1);
    }

    [Test]
    public async Task SaveAsync_RefreshCaptureResetsReferencesButNewCaseKeepsExplicitOnes()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "reference-key"),
            CancellationToken.None
        );

        var withReferences = await store.SaveAsync(
            NewSaveRequest(
                dataset,
                [
                    NewCase(
                        901,
                        agent.Id,
                        "sha-1",
                        [
                            new CodeReviewEvaluationReferenceSet(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [
                                    new CodeReviewEvaluationReferenceFinding(
                                        "ref_1",
                                        "Null check missing",
                                        "The new branch dereferences a possibly null value.",
                                        CodeReviewEvaluationFindingSeverity.Major,
                                        "src/App.cs",
                                        12,
                                        CodeReviewEvaluationReferenceOrigin.Manual,
                                        null,
                                        null,
                                        "usr_1",
                                        _clock.GetUtcNow()
                                    ),
                                ]
                            ),
                        ]
                    ),
                ]
            ),
            CancellationToken.None
        );

        var created = withReferences.Cases!.Single();
        await Assert.That(created.ReferenceSets.Single().Findings.Count).IsEqualTo(1);

        var refreshed = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: 2,
                dataset.Name,
                null,
                [new(created.Id, 901, NewCapture("sha-2", 901), null)]
            ),
            CancellationToken.None
        );

        await Assert
            .That(refreshed.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        await Assert.That(refreshed.Cases!.Single().Id).IsEqualTo(created.Id);
        await Assert.That(refreshed.Cases!.Single().ReferenceSets.Count).IsEqualTo(0);
        await Assert.That(refreshed.Cases!.Single().HeadSha).IsEqualTo("head-sha-sha-2");
    }

    [Test]
    public async Task SaveAsync_RejectsCaseIdPointingAtAnotherPullRequest()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "mismatch-key"),
            CancellationToken.None
        );

        var added = await store.SaveAsync(
            NewSaveRequest(dataset, [NewCase(901, agent.Id, "sha-1")]),
            CancellationToken.None
        );
        var caseId = added.Cases!.Single().Id;

        var mismatched = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: 2,
                dataset.Name,
                null,
                [new(caseId, 902, null, null)]
            ),
            CancellationToken.None
        );

        await Assert
            .That(mismatched.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);
    }

    [Test]
    public async Task RunStore_AdmitFreezesReferenceContentAndRejectsASecondActiveRun()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "admit-key");
        var store = Runs;

        var admitted = await store.AdmitAsync(
            NewAdmission(dataset, agent, "request-1"),
            CancellationToken.None
        );

        await Assert
            .That(admitted.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.Admitted);
        await Assert.That(admitted.Run).IsNotNull();
        await Assert.That(admitted.Run!.State).IsEqualTo(CodeReviewEvaluationRunState.Queued);
        await Assert.That(admitted.Run.CaseCount).IsEqualTo(1);

        var detail = await store.FindAsync(
            repository.OrganizationId,
            admitted.Run.Id,
            CancellationToken.None
        );
        var manifestCase = detail!.Manifest.Cases.Single();
        await Assert
            .That(manifestCase.References.State)
            .IsEqualTo(CodeReviewEvaluationReferenceState.Expected);
        await Assert.That(manifestCase.References.Findings.Single().Id).IsEqualTo("ref_1");

        var duplicateKey = await store.AdmitAsync(
            NewAdmission(dataset, agent, "request-1"),
            CancellationToken.None
        );
        await Assert
            .That(duplicateKey.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey);
        await Assert.That(duplicateKey.Run!.Id).IsEqualTo(admitted.Run.Id);

        var conflict = await store.AdmitAsync(
            NewAdmission(dataset, agent, "request-2"),
            CancellationToken.None
        );
        await Assert
            .That(conflict.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict);
        await Assert.That(conflict.ActiveRunId).IsEqualTo(admitted.Run.Id);
    }

    [Test]
    public async Task RunStore_FencedWritesRejectStaleTokensAndFinalizeComputesCounts()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "fence-key");
        var store = Runs;
        var admitted = await store.AdmitAsync(
            NewAdmission(dataset, agent, "fence-request"),
            CancellationToken.None
        );
        var run = admitted.Run!;
        var manifestCaseId = (
            await store.FindAsync(repository.OrganizationId, run.Id, CancellationToken.None)
        )!
            .Manifest.Cases.Single()
            .CaseId;

        var claim = await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Assert.That(claim.Outcome).IsEqualTo(CodeReviewEvaluationRunClaimOutcome.Claimed);

        var duplicate = await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-2",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Assert
            .That(duplicate.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunClaimOutcome.LeaseActive);

        await Assert.ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
            await Results.StartReviewAsync(
                new(repository.OrganizationId, run.Id, manifestCaseId, "lease-2"),
                CancellationToken.None
            )
        );

        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, manifestCaseId, "lease-1"),
            CancellationToken.None
        );
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                run.Id,
                manifestCaseId,
                "lease-1",
                new CodeReviewEvaluationArtifact(
                    "postgres://x/output.xml",
                    new string('b', 64),
                    10
                ),
                1,
                "[]",
                "{}",
                100,
                20,
                0.01m
            ),
            CancellationToken.None
        );
        await Results.StartGradingAsync(
            new(repository.OrganizationId, run.Id, manifestCaseId, "lease-1", null),
            CancellationToken.None
        );
        await Results.RecordGradeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                manifestCaseId,
                "lease-1",
                1,
                0,
                1,
                0,
                "[{\"referenceId\":\"ref_1\",\"candidateId\":\"c_1\"}]",
                "[]",
                "{}",
                50,
                10,
                0.02m
            ),
            CancellationToken.None
        );

        var finalized = await store.FinalizeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                "lease-1",
                CodeReviewEvaluationRunState.Completed,
                null,
                null
            ),
            CancellationToken.None
        );

        await Assert.That(finalized.State).IsEqualTo(CodeReviewEvaluationRunState.Completed);
        await Assert.That(finalized.GradedCaseCount).IsEqualTo(1);
        await Assert.That(finalized.ExpectedReferenceCount).IsEqualTo(1);
        await Assert.That(finalized.MatchedReferenceCount).IsEqualTo(1);
        await Assert.That(finalized.FailedCaseCount).IsEqualTo(0);

        var renewAfterFinalize = await store.RenewLeaseAsync(
            repository.OrganizationId,
            run.Id,
            "lease-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Assert.That(renewAfterFinalize.Renewed).IsFalse();
    }

    [Test]
    public async Task RunStore_CancelWhileRunningBlocksNewCasesAndFinalizesCancelled()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "cancel-key", caseCount: 2);
        var store = Runs;
        var admitted = await store.AdmitAsync(
            NewAdmission(dataset, agent, "cancel-request"),
            CancellationToken.None
        );
        var run = admitted.Run!;
        var manifest = (
            await store.FindAsync(repository.OrganizationId, run.Id, CancellationToken.None)
        )!.Manifest;
        var firstCase = manifest.Cases[0].CaseId;
        var secondCase = manifest.Cases[1].CaseId;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-c",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, firstCase, "lease-c"),
            CancellationToken.None
        );

        var renewal = await store.RenewLeaseAsync(
            repository.OrganizationId,
            run.Id,
            "lease-c",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Assert.That(renewal.Renewed).IsTrue();
        await Assert.That(renewal.CancelRequested).IsFalse();

        await store.RequestCancelAsync(repository.OrganizationId, run.Id, CancellationToken.None);

        // The in-flight case may still finish, but no new case may start.
        await Assert.ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
            await Results.StartReviewAsync(
                new(repository.OrganizationId, run.Id, secondCase, "lease-c"),
                CancellationToken.None
            )
        );

        await Results.RecordFailureAsync(
            new(
                repository.OrganizationId,
                run.Id,
                firstCase,
                "lease-c",
                CodeReviewEvaluationCaseOutcome.ReviewFailed,
                "ReviewerFailed",
                "Fixture failure"
            ),
            CancellationToken.None
        );

        // A cancellation that lands before finalization must win over Completed.
        var finalized = await store.FinalizeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                "lease-c",
                CodeReviewEvaluationRunState.Completed,
                null,
                null
            ),
            CancellationToken.None
        );

        await Assert.That(finalized.State).IsEqualTo(CodeReviewEvaluationRunState.Cancelled);
        await Assert.That(finalized.FailedCaseCount).IsEqualTo(1);
    }

    [Test]
    public async Task RunStore_FinalizeUsesFreshLeaseStateAfterExternalRenewal()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "renew-key");
        var store = Runs;
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "renew-request"),
                CancellationToken.None
            )
        ).Run!;

        // Short lease, then a renewal performed by another scope (simulated by a direct
        // update). A tracked-snapshot finalize would still see the short expiry and fail.
        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-r",
            TimeSpan.FromSeconds(1),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        await _context
            .CodeReviewEvaluationRuns.Where(candidate =>
                candidate.OrganizationId == repository.OrganizationId && candidate.Id == run.Id
            )
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(
                    candidate => candidate.LeaseExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddMinutes(10)
                )
            );

        var finalized = await store.FinalizeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                "lease-r",
                CodeReviewEvaluationRunState.Incomplete,
                null,
                null
            ),
            CancellationToken.None
        );

        await Assert.That(finalized.State).IsEqualTo(CodeReviewEvaluationRunState.Incomplete);
        await Assert.That(finalized.LeaseToken).IsNull();
    }

    [Test]
    public async Task RunStore_FinalizeRejectsExpiredLeaseAndAbandonRejectsLiveLease()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "expiry-key");
        var store = Runs;
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "expiry-request"),
                CancellationToken.None
            )
        ).Run!;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-e",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );

        // A live owner must never be abandoned: freeing admission would overlap provider calls.
        await Assert.ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
            await store.AbandonAsync(
                repository.OrganizationId,
                run.Id,
                "operator_abandon",
                CancellationToken.None
            )
        );

        _context.ChangeTracker.Clear();
        await _context
            .CodeReviewEvaluationRuns.Where(candidate =>
                candidate.OrganizationId == repository.OrganizationId && candidate.Id == run.Id
            )
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(
                    candidate => candidate.LeaseExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddMinutes(-1)
                )
            );

        await Assert.ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
            await store.FinalizeAsync(
                new(
                    repository.OrganizationId,
                    run.Id,
                    "lease-e",
                    CodeReviewEvaluationRunState.Completed,
                    null,
                    null
                ),
                CancellationToken.None
            )
        );

        // An expired lease is abandonable, and that frees admission.
        var abandoned = await store.AbandonAsync(
            repository.OrganizationId,
            run.Id,
            "lease_expired",
            CancellationToken.None
        );
        await Assert.That(abandoned.State).IsEqualTo(CodeReviewEvaluationRunState.Abandoned);
    }

    [Test]
    public async Task RunStore_RetryIncludesInterruptedCancelledCasesButNeverGradedOnes()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "retry-key", caseCount: 2);
        var store = Runs;
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "retry-request"),
                CancellationToken.None
            )
        ).Run!;
        var manifest = (
            await store.FindAsync(repository.OrganizationId, run.Id, CancellationToken.None)
        )!.Manifest;
        var gradedCase = manifest.Cases[0].CaseId;
        var interruptedCase = manifest.Cases[1].CaseId;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-t",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );

        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, gradedCase, "lease-t"),
            CancellationToken.None
        );
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                run.Id,
                gradedCase,
                "lease-t",
                new CodeReviewEvaluationArtifact("postgres://x/out.xml", new string('d', 64), 4),
                0,
                "[]",
                "{}",
                1,
                1,
                0.01m
            ),
            CancellationToken.None
        );
        await Results.StartGradingAsync(
            new(repository.OrganizationId, run.Id, gradedCase, "lease-t", null),
            CancellationToken.None
        );
        await Results.RecordGradeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                gradedCase,
                "lease-t",
                1,
                0,
                1,
                0,
                "[]",
                "[]",
                "{}",
                1,
                1,
                0.01m
            ),
            CancellationToken.None
        );

        // Expire the lease first: abandoning a live owner is rejected by design.
        _context.ChangeTracker.Clear();
        await _context
            .CodeReviewEvaluationRuns.Where(candidate =>
                candidate.OrganizationId == repository.OrganizationId && candidate.Id == run.Id
            )
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(
                    candidate => candidate.LeaseExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddMinutes(-1)
                )
            );

        // Abandoning converts the untouched case to Cancelled, which is an interrupted case and
        // must remain retryable; the graded case must not be.
        await store.AbandonAsync(
            repository.OrganizationId,
            run.Id,
            "operator_abandon",
            CancellationToken.None
        );

        var retry = await store.AdmitRetryAsync(
            new(
                repository.OrganizationId,
                run.Id,
                "retry-request-2",
                "usr_1",
                "build-test",
                "prompt-v1",
                "fingerprint-retry"
            ),
            CancellationToken.None
        );

        await Assert
            .That(retry.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.Admitted);
        var retryManifest = (
            await store.FindAsync(repository.OrganizationId, retry.Run!.Id, CancellationToken.None)
        )!.Manifest;
        await Assert.That(retryManifest.ParentRunId).IsEqualTo(run.Id);
        await Assert.That(retryManifest.Cases.Count).IsEqualTo(1);
        await Assert.That(retryManifest.Cases.Single().CaseId).IsEqualTo(interruptedCase);
    }

    [Test]
    public async Task RunStore_LeaseExpiryUsesDatabaseClockNotReplicaClock()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "skew-key");

        // A replica one hour ahead must not grant an hour of extra lease time.
        var skewedClock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        var store = new PostgresCodeReviewEvaluationRunStore(_context, skewedClock);
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "skew-request"),
                CancellationToken.None
            )
        ).Run!;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-s",
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var claimed = await _context
            .CodeReviewEvaluationRuns.AsNoTracking()
            .Where(candidate =>
                candidate.OrganizationId == repository.OrganizationId && candidate.Id == run.Id
            )
            .SingleAsync();

        var remaining = (claimed.LeaseExpiresAtUtc!.Value - DateTimeOffset.UtcNow).TotalSeconds;
        await Assert.That(remaining).IsGreaterThan(0);
        await Assert.That(remaining).IsLessThan(120);

        // Renewal follows the same rule.
        await store.RenewLeaseAsync(
            repository.OrganizationId,
            run.Id,
            "lease-s",
            TimeSpan.FromSeconds(60),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var renewed = await _context
            .CodeReviewEvaluationRuns.AsNoTracking()
            .Where(candidate =>
                candidate.OrganizationId == repository.OrganizationId && candidate.Id == run.Id
            )
            .SingleAsync();

        var renewedRemaining = (
            renewed.LeaseExpiresAtUtc!.Value - DateTimeOffset.UtcNow
        ).TotalSeconds;
        await Assert.That(renewedRemaining).IsLessThan(180);
    }

    [Test]
    public async Task SaveAsync_RejectsCaptureForAnotherPullRequest()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "token-bind-key"),
            CancellationToken.None
        );

        var saved = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: 1,
                dataset.Name,
                null,
                // Capture says 901, the membership says 902.
                [new(null, 902, NewCapture("sha-x", 901), null)]
            ),
            CancellationToken.None
        );

        await Assert
            .That(saved.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);
    }

    [Test]
    public async Task SaveAsync_PreLockReadDoesNotLeakStaleVersion()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "stale-read-key"),
            CancellationToken.None
        );

        // Handler-style read in the same scope, then a save that advances the version.
        var preLockRead = await store.FindAsync(
            dataset.OrganizationId,
            dataset.Id,
            includeArchived: false,
            CancellationToken.None
        );
        await Assert.That(preLockRead!.Version).IsEqualTo(1);

        var firstSave = await store.SaveAsync(
            NewSaveRequest(preLockRead, [NewCase(901, agent.Id, "sha-1")]),
            CancellationToken.None
        );
        await Assert
            .That(firstSave.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);

        // The earlier read must not be reused as a successful version claim.
        var staleSave = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                ExpectedVersion: preLockRead.Version,
                dataset.Name,
                null,
                [NewCase(901, agent.Id, "sha-2")]
            ),
            CancellationToken.None
        );
        await Assert
            .That(staleSave.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.VersionConflict);

        var fresh = await store.FindAsync(
            dataset.OrganizationId,
            dataset.Id,
            includeArchived: false,
            CancellationToken.None
        );
        await Assert.That(fresh!.Version).IsEqualTo(2);
    }

    [Test]
    public async Task ArtifactStore_RejectsSubstitutedArtifactBytes()
    {
        var (repository, _) = await SeedRepositoryAsync();
        var snapshot = new CodeReviewEvaluationSnapshot(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            repository.OrganizationId,
            repository.Id,
            repository.OwnerQualifiedName,
            901,
            "Title",
            "Body",
            "base",
            "head",
            "octocat",
            "https://github.com/example/pull/901",
            false,
            PullRequestState.Open,
            _clock.GetUtcNow(),
            1,
            [new("src/App.cs", null, CodeReviewFileMutationState.Modified, "@@ -1 +1 @@", false)]
        );

        var artifact = await Artifacts.WriteSnapshotAsync(
            repository.OrganizationId,
            repository.Id,
            snapshot,
            CancellationToken.None
        );
        var read = await Artifacts.ReadSnapshotAsync(
            repository.OrganizationId,
            repository.Id,
            artifact,
            CancellationToken.None
        );

        await Assert.That(read.HeadSha).IsEqualTo("head");
        await Assert.That(read.Files.Count).IsEqualTo(1);

        await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(async () =>
            await Artifacts.ReadSnapshotAsync(
                repository.OrganizationId,
                repository.Id,
                artifact with
                {
                    Sha256 = new string('0', 64),
                },
                CancellationToken.None
            )
        );
    }

    [Test]
    public async Task RunStore_GradeWithNullUsageAndCost_PersistsWithoutFabricatingValues()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "null-usage-key");
        var store = Runs;
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "null-usage-request"),
                CancellationToken.None
            )
        ).Run!;
        var caseId = (
            await store.FindAsync(repository.OrganizationId, run.Id, CancellationToken.None)
        )!
            .Manifest.Cases.Single()
            .CaseId;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-n",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, caseId, "lease-n"),
            CancellationToken.None
        );

        // A deterministic fake judge reports no usage and no cost. Null must bind as a typed
        // null, not an untyped parameter: the CASE expression cannot infer DBNull.
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                "lease-n",
                new CodeReviewEvaluationArtifact(
                    "postgres://x/null-output.xml",
                    new string('e', 64),
                    2
                ),
                0,
                "[]",
                "{}",
                InputTokens: null,
                OutputTokens: null,
                EstimatedCostUsd: null
            ),
            CancellationToken.None
        );
        await Results.StartGradingAsync(
            new(repository.OrganizationId, run.Id, caseId, "lease-n", null),
            CancellationToken.None
        );
        var graded = await Results.RecordGradeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                "lease-n",
                0,
                1,
                1,
                0,
                "[]",
                "[]",
                "{}",
                InputTokens: null,
                OutputTokens: null,
                EstimatedCostUsd: null
            ),
            CancellationToken.None
        );

        await Assert.That(graded.Outcome).IsEqualTo(CodeReviewEvaluationCaseOutcome.Graded);
        await Assert.That(graded.EstimatedCostUsd).IsNull();
        await Assert.That(graded.JudgeInputTokens).IsNull();
        await Assert.That(graded.JudgeOutputTokens).IsNull();
    }

    [Test]
    public async Task RunStore_TerminalAckRequiresMatchingOutcome()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "ack-key");
        var store = Runs;
        var run = (
            await store.AdmitAsync(
                NewAdmission(dataset, agent, "ack-request"),
                CancellationToken.None
            )
        ).Run!;
        var caseId = (
            await store.FindAsync(repository.OrganizationId, run.Id, CancellationToken.None)
        )!
            .Manifest.Cases.Single()
            .CaseId;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-a",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, caseId, "lease-a"),
            CancellationToken.None
        );
        await Results.RecordFailureAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                "lease-a",
                CodeReviewEvaluationCaseOutcome.ReviewFailed,
                "ReviewerFailed",
                "Fixture failure"
            ),
            CancellationToken.None
        );

        // Repeating the same terminal transition is an idempotent ack.
        var repeated = await Results.RecordFailureAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                "lease-a",
                CodeReviewEvaluationCaseOutcome.ReviewFailed,
                "ReviewerFailed",
                "Fixture failure"
            ),
            CancellationToken.None
        );
        await Assert.That(repeated.Outcome).IsEqualTo(CodeReviewEvaluationCaseOutcome.ReviewFailed);

        // A different terminal transition must not report success.
        await Assert.ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
            await Results.RecordSkipAsync(
                new(repository.OrganizationId, run.Id, caseId, "lease-a", "NoFilesInScope"),
                CancellationToken.None
            )
        );
    }

    [Test]
    public async Task SaveAsync_ValidatesPromotedFindingProvenance()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "promotion-key"),
            CancellationToken.None
        );
        var seeded = await store.SaveAsync(
            NewSaveRequest(dataset, [NewCase(901, agent.Id, "sha-p")]),
            CancellationToken.None
        );
        var caseId = seeded.Cases!.Single().Id;
        var version = seeded.Dataset!.Version;

        var runStore = Runs;
        var run = (
            await runStore.AdmitAsync(
                NewAdmission(seeded.Dataset, agent, "promotion-run"),
                CancellationToken.None
            )
        ).Run!;
        await runStore.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            "lease-p",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, caseId, "lease-p"),
            CancellationToken.None
        );
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                "lease-p",
                new CodeReviewEvaluationArtifact("postgres://x/p.xml", new string('f', 64), 3),
                1,
                "[{\"id\":\"cand_1\"}]",
                "{}",
                1,
                1,
                0.01m
            ),
            CancellationToken.None
        );

        // A source finding that does not exist in the referenced run is rejected.
        var spoofed = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                seeded.Dataset.Name,
                null,
                [
                    new(
                        caseId,
                        901,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_spoof", run.Id, "cand_missing")]
                            ),
                        ]
                    ),
                ]
            ),
            CancellationToken.None
        );
        await Assert
            .That(spoofed.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);

        // A source run from another organization is rejected the same way.
        var foreignRun = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                seeded.Dataset.Name,
                null,
                [
                    new(
                        caseId,
                        901,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_foreign", "ceref_other_org", "cand_1")]
                            ),
                        ]
                    ),
                ]
            ),
            CancellationToken.None
        );
        await Assert
            .That(foreignRun.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);

        // A real candidate finding from the same organization and repository is accepted.
        var valid = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                seeded.Dataset.Name,
                null,
                [
                    new(
                        caseId,
                        901,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_ok", run.Id, "cand_1")]
                            ),
                        ]
                    ),
                ]
            ),
            CancellationToken.None
        );
        await Assert.That(valid.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        await Assert
            .That(valid.Cases!.Single().ReferenceSets.Single().Findings.Single().Id)
            .IsEqualTo("ref_ok");
    }

    private static CodeReviewEvaluationReferenceFinding PromotionFinding(
        string id,
        string sourceRunId,
        string sourceFindingId
    ) =>
        new(
            id,
            "Promoted finding",
            "Finding adopted from an evaluation run.",
            CodeReviewEvaluationFindingSeverity.Major,
            "src/Search.cs",
            12,
            CodeReviewEvaluationReferenceOrigin.RunFinding,
            sourceRunId,
            sourceFindingId,
            "usr_1",
            DateTimeOffset.UtcNow
        );

    [Test]
    public async Task RunStore_MarkDispatchFailedFreesQueuedSlotButNeverOverwritesRunning()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "dispatch-key");
        var store = Runs;

        var first = await store.AdmitAsync(
            NewAdmission(dataset, agent, "dispatch-request-1"),
            CancellationToken.None
        );
        await Assert
            .That(first.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.Admitted);

        // The queued run occupies the organization slot.
        var blocked = await store.AdmitAsync(
            NewAdmission(dataset, agent, "dispatch-request-2"),
            CancellationToken.None
        );
        await Assert
            .That(blocked.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.ActiveRunConflict);

        var failed = await store.MarkDispatchFailedAsync(
            repository.OrganizationId,
            first.Run!.Id,
            "PublishFailed",
            CancellationToken.None
        );
        await Assert.That(failed.State).IsEqualTo(CodeReviewEvaluationRunState.DispatchFailed);

        // A dispatch failure frees admission for the next attempt.
        var second = await store.AdmitAsync(
            NewAdmission(dataset, agent, "dispatch-request-2"),
            CancellationToken.None
        );
        await Assert
            .That(second.Outcome)
            .IsEqualTo(CodeReviewEvaluationRunAdmissionOutcome.Admitted);

        // Once a consumer owns the run, a stale dispatch failure must not overwrite it.
        var claim = await store.TryClaimAsync(
            repository.OrganizationId,
            second.Run!.Id,
            "lease-d",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Assert.That(claim.Outcome).IsEqualTo(CodeReviewEvaluationRunClaimOutcome.Claimed);

        var afterClaim = await store.MarkDispatchFailedAsync(
            repository.OrganizationId,
            second.Run.Id,
            "PublishFailed",
            CancellationToken.None
        );
        await Assert.That(afterClaim.State).IsEqualTo(CodeReviewEvaluationRunState.Running);
        await Assert.That(afterClaim.LeaseToken).IsEqualTo("lease-d");
    }

    [Test]
    public async Task DatasetStore_ArchivePersistsAndExcludesFromListAndSave()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "archive-key"),
            CancellationToken.None
        );

        var archived = await store.ArchiveAsync(
            repository.OrganizationId,
            dataset.Id,
            CancellationToken.None
        );
        await Assert.That(archived).IsNotNull();
        await Assert.That(archived!.ArchivedAtUtc).IsNotNull();

        _context.ChangeTracker.Clear();
        var fresh = await store.FindAsync(
            repository.OrganizationId,
            dataset.Id,
            includeArchived: true,
            CancellationToken.None
        );
        await Assert.That(fresh!.ArchivedAtUtc).IsNotNull();

        await Assert
            .That(
                await store.ListAsync(
                    new(repository.OrganizationId, repository.Id, IncludeArchived: false),
                    CancellationToken.None
                )
            )
            .IsEmpty();
        await Assert
            .That(
                await store.ListAsync(
                    new(repository.OrganizationId, repository.Id, IncludeArchived: true),
                    CancellationToken.None
                )
            )
            .Count()
            .IsEqualTo(1);

        // An archived dataset cannot be saved, even with the right version.
        var save = await store.SaveAsync(
            NewSaveRequest(fresh, [NewCase(901, agent.Id, "sha-arch")]),
            CancellationToken.None
        );
        await Assert
            .That(save.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.DatasetNotFound);
    }

    [Test]
    public async Task SaveAsync_PromotionRequiresMatchingSourceRunAndPullRequest()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, "promotion-pair-key"),
            CancellationToken.None
        );
        var first = await store.SaveAsync(
            NewSaveRequest(dataset, [NewCase(901, agent.Id, "sha-901")]),
            CancellationToken.None
        );
        var case901 = first.Cases!.Single().Id;
        var version = first.Dataset!.Version;

        var runA = await RunToTerminalAsync(
            repository,
            agent,
            first.Dataset,
            case901,
            "promotion-run-a",
            "lease-a1"
        );

        var second = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                dataset.Name,
                null,
                [
                    new(case901, 901, null, null),
                    new(null, 902, NewCapture("sha-902", 902), null),
                ]
            ),
            CancellationToken.None
        );
        await Assert.That(second.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        var case902 = second.Cases!.Single(item => item.PullRequestNumber == 902).Id;
        version = second.Dataset!.Version;

        var runB = await RunToTerminalAsync(
            repository,
            agent,
            second.Dataset!,
            case902,
            "promotion-run-b",
            "lease-b1"
        );

        // The same finding id exists in run A, but only for PR 901. Claiming run B's finding for
        // the PR 901 case is rejected: run B has finding_1 only for PR 902.
        var wrongRun = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                dataset.Name,
                null,
                [
                    new(
                        case901,
                        901,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_wrong_run", runB, "finding_1")]
                            ),
                        ]
                    ),
                    new(case902, 902, null, null),
                ]
            ),
            CancellationToken.None
        );
        await Assert
            .That(wrongRun.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);

        // Run A has finding_1 only for PR 901; claiming it for PR 902 is rejected.
        var wrongPullRequest = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                dataset.Name,
                null,
                [
                    new(case901, 901, null, null),
                    new(
                        case902,
                        902,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_wrong_pr", runA, "finding_1")]
                            ),
                        ]
                    ),
                ]
            ),
            CancellationToken.None
        );
        await Assert
            .That(wrongPullRequest.Outcome)
            .IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.InvalidCase);

        // The matching run and pull request is accepted.
        var valid = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                version,
                dataset.Name,
                null,
                [
                    new(
                        case901,
                        901,
                        null,
                        [
                            new(
                                agent.Id,
                                CodeReviewEvaluationReferenceState.Expected,
                                [PromotionFinding("ref_pair_ok", runA, "finding_1")]
                            ),
                        ]
                    ),
                    new(case902, 902, null, null),
                ]
            ),
            CancellationToken.None
        );
        await Assert.That(valid.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
    }

    private async Task<string> RunToTerminalAsync(
        CodeRepository repository,
        CodeReviewerAgent agent,
        CodeReviewEvaluationDataset dataset,
        string caseId,
        string requestKey,
        string leaseToken
    )
    {
        var store = Runs;
        var run = (
            await store.AdmitAsync(NewAdmission(dataset, agent, requestKey), CancellationToken.None)
        ).Run!;

        await store.TryClaimAsync(
            repository.OrganizationId,
            run.Id,
            leaseToken,
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        await Results.StartReviewAsync(
            new(repository.OrganizationId, run.Id, caseId, leaseToken),
            CancellationToken.None
        );
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                leaseToken,
                new CodeReviewEvaluationArtifact(
                    $"postgres://reviewer-evaluations/org/repo/{requestKey}/output.xml",
                    new string('9', 64),
                    64
                ),
                1,
                "[{\"id\":\"finding_1\"}]",
                "{}",
                5,
                2,
                0.01m
            ),
            CancellationToken.None
        );
        await Results.StartGradingAsync(
            new(repository.OrganizationId, run.Id, caseId, leaseToken, null),
            CancellationToken.None
        );
        await Results.RecordGradeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                caseId,
                leaseToken,
                1,
                0,
                1,
                0,
                "[]",
                "[]",
                "{}",
                1,
                1,
                0.01m
            ),
            CancellationToken.None
        );
        await store.FinalizeAsync(
            new(
                repository.OrganizationId,
                run.Id,
                leaseToken,
                CodeReviewEvaluationRunState.Completed,
                null,
                null
            ),
            CancellationToken.None
        );

        return run.Id;
    }

    [Test]
    public async Task RunStore_FailureOutcomeMustMatchExecutionStage()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var dataset = await SeedDatasetWithCaseAsync(repository, agent, "failure-stage-key");
        var store = Runs;

        async Task<(CodeReviewEvaluationRun Run, string CaseId, string Lease)> StartAsync(
            string requestKey
        )
        {
            var run = (
                await store.AdmitAsync(
                    NewAdmission(dataset, agent, requestKey),
                    CancellationToken.None
                )
            ).Run!;
            var lease = $"lease-{requestKey}";
            await store.TryClaimAsync(
                repository.OrganizationId,
                run.Id,
                lease,
                TimeSpan.FromMinutes(5),
                CancellationToken.None
            );
            var caseId = (await store.FindAsync(
                repository.OrganizationId,
                run.Id,
                CancellationToken.None
            ))!.Manifest.Cases.Single().CaseId;

            return (run, caseId, lease);
        }

        async Task FinishAsync(CodeReviewEvaluationRun run, string lease)
        {
            await store.FinalizeAsync(
                new(
                    repository.OrganizationId,
                    run.Id,
                    lease,
                    CodeReviewEvaluationRunState.Incomplete,
                    null,
                    null
                ),
                CancellationToken.None
            );
        }

        // Pre-start failure path: the worker can durably record a reviewer failure before the
        // reviewer stage ever starts.
        var pending = await StartAsync("failure-pending");
        var pendingFailure = await Results.RecordFailureAsync(
            new(
                repository.OrganizationId,
                pending.Run.Id,
                pending.CaseId,
                pending.Lease,
                CodeReviewEvaluationCaseOutcome.ReviewFailed,
                "SnapshotIntegrityFailed",
                "Snapshot bytes failed verification"
            ),
            CancellationToken.None
        );
        await Assert
            .That(pendingFailure.Outcome)
            .IsEqualTo(CodeReviewEvaluationCaseOutcome.ReviewFailed);
        await Assert.That(pendingFailure.Phase).IsEqualTo(CodeReviewEvaluationCasePhase.Pending);
        await FinishAsync(pending.Run, pending.Lease);

        // Reviewer failure while reviewing is also valid.
        var reviewing = await StartAsync("failure-reviewing");
        await Results.StartReviewAsync(
            new(repository.OrganizationId, reviewing.Run.Id, reviewing.CaseId, reviewing.Lease),
            CancellationToken.None
        );
        var reviewingFailure = await Results.RecordFailureAsync(
            new(
                repository.OrganizationId,
                reviewing.Run.Id,
                reviewing.CaseId,
                reviewing.Lease,
                CodeReviewEvaluationCaseOutcome.ReviewFailed,
                "ReviewerFailed",
                "Reviewer threw"
            ),
            CancellationToken.None
        );
        await Assert
            .That(reviewingFailure.Outcome)
            .IsEqualTo(CodeReviewEvaluationCaseOutcome.ReviewFailed);
        await FinishAsync(reviewing.Run, reviewing.Lease);

        // A reviewer failure after the candidate exists is rejected: that shape is a judge
        // failure and must not be reported as a reviewer failure.
        var afterCandidate = await StartAsync("failure-after-candidate");
        await Results.StartReviewAsync(
            new(
                repository.OrganizationId,
                afterCandidate.Run.Id,
                afterCandidate.CaseId,
                afterCandidate.Lease
            ),
            CancellationToken.None
        );
        await Results.RecordCandidateAsync(
            new(
                repository.OrganizationId,
                afterCandidate.Run.Id,
                afterCandidate.CaseId,
                afterCandidate.Lease,
                new CodeReviewEvaluationArtifact("postgres://x/f.xml", new string('7', 64), 8),
                0,
                "[]",
                "{}",
                1,
                1,
                0.01m
            ),
            CancellationToken.None
        );
        await Assert
            .ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
                await Results.RecordFailureAsync(
                    new(
                        repository.OrganizationId,
                        afterCandidate.Run.Id,
                        afterCandidate.CaseId,
                        afterCandidate.Lease,
                        CodeReviewEvaluationCaseOutcome.ReviewFailed,
                        "ReviewerFailed",
                        "Late reviewer failure"
                    ),
                    CancellationToken.None
                )
            );
        await FinishAsync(afterCandidate.Run, afterCandidate.Lease);

        // A judge failure before grading starts is rejected.
        var beforeGrading = await StartAsync("failure-before-grading");
        await Results.StartReviewAsync(
            new(
                repository.OrganizationId,
                beforeGrading.Run.Id,
                beforeGrading.CaseId,
                beforeGrading.Lease
            ),
            CancellationToken.None
        );
        await Assert
            .ThrowsAsync<CodeReviewEvaluationFencedException>(async () =>
                await Results.RecordFailureAsync(
                    new(
                        repository.OrganizationId,
                        beforeGrading.Run.Id,
                        beforeGrading.CaseId,
                        beforeGrading.Lease,
                        CodeReviewEvaluationCaseOutcome.JudgeFailed,
                        "JudgeInvalidJson",
                        "Judge ran too early"
                    ),
                    CancellationToken.None
                )
            );
        await FinishAsync(beforeGrading.Run, beforeGrading.Lease);
    }

    private async Task<(CodeRepository Repository, CodeReviewerAgent Agent)> SeedRepositoryAsync()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository)
            .BuildAsync();

        var now = _clock.GetUtcNow();
        var agent = new CodeReviewerAgent
        {
            Id = $"cra_{Guid.CreateVersion7():N}",
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            DisplayName = "Fixture reviewer",
            ReviewFacet = "Correctness",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review for correctness.",
            Enabled = true,
            ActivationConfiguration = CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        _context.CodeReviewerAgents.Add(agent);
        await _context.SaveChangesAsync();

        _ = seed;
        return (repository, agent);
    }

    private async Task<CodeReviewEvaluationDataset> SeedDatasetWithCaseAsync(
        CodeRepository repository,
        CodeReviewerAgent agent,
        string requestKey,
        int caseCount = 1
    )
    {
        var store = Datasets;
        var dataset = await store.CreateAsync(
            NewDataset(repository, requestKey),
            CancellationToken.None
        );
        var cases = Enumerable
            .Range(0, caseCount)
            .Select(index =>
                NewCase(
                    901 + index,
                    agent.Id,
                    $"sha-{requestKey}-{index}",
                    [
                        new CodeReviewEvaluationReferenceSet(
                            agent.Id,
                            CodeReviewEvaluationReferenceState.Expected,
                            [
                                new CodeReviewEvaluationReferenceFinding(
                                    "ref_1",
                                    "Null check missing",
                                    "The new branch dereferences a possibly null value.",
                                    CodeReviewEvaluationFindingSeverity.Major,
                                    "src/App.cs",
                                    12,
                                    CodeReviewEvaluationReferenceOrigin.Manual,
                                    null,
                                    null,
                                    "usr_1",
                                    _clock.GetUtcNow()
                                ),
                            ]
                        ),
                    ]
                )
            )
            .ToArray();

        var saved = await store.SaveAsync(NewSaveRequest(dataset, cases), CancellationToken.None);

        return saved.Dataset!;
    }

    private static CodeReviewEvaluationDataset NewDataset(
        CodeRepository repository,
        string createRequestKey
    ) =>
        new()
        {
            Id = $"cred_{Guid.CreateVersion7():N}",
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            Name = "Fixture dataset",
            Version = 1,
            CreateRequestKey = createRequestKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static CodeReviewEvaluationDatasetSaveRequest NewSaveRequest(
        CodeReviewEvaluationDataset dataset,
        IReadOnlyList<CodeReviewEvaluationCaseSave> cases
    ) =>
        new(
            dataset.OrganizationId,
            dataset.RepositoryId,
            dataset.Id,
            dataset.Version,
            dataset.Name,
            dataset.Description,
            cases
        );

    private static CodeReviewEvaluationCaseSave NewCase(
        int pullRequestNumber,
        string reviewerAgentId,
        string shaSeed,
        IReadOnlyList<CodeReviewEvaluationReferenceSet>? referenceSets = null
    ) => new(null, pullRequestNumber, NewCapture(shaSeed, pullRequestNumber), referenceSets);

    private static CodeReviewEvaluationStagedCapture NewCapture(
        string shaSeed,
        int pullRequestNumber = 901
    ) =>
        new(
            new CodeReviewEvaluationArtifact(
                $"postgres://reviewer-evaluations/org/repo/{shaSeed}/snapshot.json",
                Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(shaSeed)
                    )
                ),
                128
            ),
            pullRequestNumber,
            $"Fixture pull request {shaSeed}",
            "octocat",
            "base-sha",
            $"head-sha-{shaSeed}",
            PullRequestState.Open,
            DateTimeOffset.UtcNow,
            ChangedFileCount: 1,
            OmittedFileCount: 0,
            DeclaredChangedFileCount: 1,
            PullRequestRecordId: null
        );

    private static CodeReviewEvaluationRunAdmission NewAdmission(
        CodeReviewEvaluationDataset dataset,
        CodeReviewerAgent agent,
        string requestKey
    ) =>
        new(
            dataset.OrganizationId,
            dataset.RepositoryId,
            dataset.Id,
            dataset.Version,
            agent.Id,
            null,
            CodeReviewerAgentRevisionContent.Capture(agent),
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
            new string('c', 64),
            "{}",
            [],
            "build-test",
            "prompt-v1",
            "usr_1",
            requestKey,
            $"fingerprint-{requestKey}"
        );
}
