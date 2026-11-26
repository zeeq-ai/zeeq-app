using Microsoft.EntityFrameworkCore;
using Pgvector;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Exercises committed concurrent triage writes and embedding lease fences.
/// Run: dotnet run --project src/backend/Zeeq.Data.Postgres.Tests -- --treenode-filter "/*/*/CodeReviewFindingTriageStoreTests/*"</summary>
[NotInParallel]
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewFindingTriageStoreTests(PgDatabaseFixture postgres)
{
    [Test]
    public async Task Migration_CreatesPartialHnswIndex()
    {
        await using var db = postgres.CreateContext();
        var count = await db
            .Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'zeeq' AND tablename = 'code_review_finding_triages' AND indexname = 'ix_cr_triages_embedding_hnsw'"
            )
            .SingleAsync();
        await Assert.That(count).IsEqualTo(1);
    }

    private sealed class Factory(PgDatabaseFixture fixture) : IDbContextFactory<PostgresDbContext>
    {
        public PostgresDbContext CreateDbContext() => fixture.CreateContext();
    }

    private async Task<string> SeedAsync()
    {
        var id = "org_" + Guid.NewGuid().ToString("N");
        await using var db = postgres.CreateContext();
        db.Users.Add(new User { Id = id, DisplayName = id });
        db.Organizations.Add(
            new Organization
            {
                Id = id,
                DisplayName = id,
                CreatedByUserId = id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        await db.SaveChangesAsync();
        return id;
    }

    private async Task CleanupAsync(string org)
    {
        await using var db = postgres.CreateContext();
        await db.CodeReviewFindingTriages.Where(x => x.OrganizationId == org).ExecuteDeleteAsync();
        await db.Organizations.Where(x => x.Id == org).ExecuteDeleteAsync();
        await db.Users.Where(x => x.Id == org).ExecuteDeleteAsync();
    }

    private static CodeReviewFindingTriage Snapshot(
        string org,
        string reviewId = "review_1",
        int? prNumber = null
    ) =>
        new()
        {
            Id = "tri_" + Guid.NewGuid().ToString("N"),
            OrganizationId = org,
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = reviewId,
            PullRequestNumber = prNumber,
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F1",
            SemanticId = "sample-finding-name",
            Facet = "security",
            Severity = CodeReviewFindingLevel.Major,
            FindingSummary = "summary",
        };

    private static FindingTriageWrite Set(
        CodeReviewFindingTriage snapshot,
        CodeReviewFindingTriageChannel channel,
        string reason
    ) =>
        new(
            snapshot,
            FindingTriageOperation.Set,
            CodeReviewFindingDisposition.FalsePositive,
            reason,
            channel,
            "user"
        );

    [Test]
    public async Task AgentAttestation_PolicyIdempotencyAndWebRevocation()
    {
        var org = await SeedAsync();
        try
        {
            await using (var db = postgres.CreateContext())
            {
                var organization = await db.Organizations.SingleAsync(x => x.Id == org);
                organization.ActivatedAtUtc = DateTimeOffset.UtcNow;
                db.Users.Add(new User { Id = "user", DisplayName = "user" });
                db.OrganizationMemberships.Add(
                    new OrganizationMembership
                    {
                        Id = Guid.NewGuid().ToString(),
                        OrganizationId = org,
                        UserId = "user",
                        CreatedByUserId = org,
                        Role = "member",
                        Status = MembershipStatus.Active,
                    }
                );
                await db.SaveChangesAsync();
            }
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            var write = Set(snapshot, CodeReviewFindingTriageChannel.AgentAttested, "reason") with
            {
                ConfirmationQuote = " Yes, defer. ",
                AgentSessionId = "session",
            };
            var applied = await store.UpsertLwwAsync(write);
            await Assert.That(applied.Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await Assert.That(applied.Row!.ConfirmationQuote).IsEqualTo(" Yes, defer. ");
            var githubOverwrite = await store.UpsertLwwAsync(
                Set(snapshot, CodeReviewFindingTriageChannel.GitHub, "reason") with
                {
                    SourceEventAtUtc = DateTimeOffset.UtcNow,
                }
            );
            await Assert.That(githubOverwrite.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
            var repeated = await store.UpsertLwwAsync(write);
            await Assert.That(repeated.Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await Assert.That(repeated.Changed).IsFalse();
            var confirmedOnWeb = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Confirm,
                    null,
                    null,
                    CodeReviewFindingTriageChannel.Ui,
                    "human",
                    ExpectedDisposition: CodeReviewFindingDisposition.FalsePositive
                )
            );
            await Assert
                .That(confirmedOnWeb.Row!.Channel)
                .IsEqualTo(CodeReviewFindingTriageChannel.Ui);
            await Assert.That(confirmedOnWeb.Row.ConfirmationQuote).IsNull();
            var overwrite = await store.UpsertLwwAsync(write);
            await Assert.That(overwrite.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
            var cleared = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Clear,
                    null,
                    null,
                    CodeReviewFindingTriageChannel.Ui,
                    "human"
                )
            );
            await Assert.That(cleared.Row!.ConfirmationQuote).IsNull();
            var replay = await store.UpsertLwwAsync(write);
            await Assert.That(replay.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
            await Assert.That(replay.Error!).Contains("web");
            var proposal = await store.UpsertLwwAsync(
                Set(snapshot, CodeReviewFindingTriageChannel.Mcp, "new proposal")
            );
            await Assert
                .That(proposal.Row!.ApprovalState)
                .IsEqualTo(CodeReviewFindingApprovalState.Proposed);
            var replayAfterProposal = await store.UpsertLwwAsync(write);
            await Assert
                .That(replayAfterProposal.Status)
                .IsEqualTo(FindingTriageWriteStatus.Forbidden);
            await using (var db = postgres.CreateContext())
            {
                var count = await db
                    .Database.SqlQuery<int>(
                        $"SELECT count(*)::int AS \"Value\" FROM zeeq.code_review_finding_attestations WHERE organization_id = {org}"
                    )
                    .SingleAsync();
                await Assert.That(count).IsEqualTo(1);
                var organization = await db.Organizations.SingleAsync(x => x.Id == org);
                organization.CodeReviewConfiguration = new CodeReviewOrganizationSettings
                {
                    AllowAgentAttestedTriage = false,
                };
                await db.SaveChangesAsync();
            }
            var denied = await store.UpsertLwwAsync(write);
            await Assert.That(denied.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
        }
        finally
        {
            await using (var db = postgres.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM zeeq.code_review_finding_attestations WHERE organization_id = {org}"
                );
                await db
                    .OrganizationMemberships.Where(x => x.OrganizationId == org)
                    .ExecuteDeleteAsync();
                await db.Users.Where(x => x.Id == "user").ExecuteDeleteAsync();
            }
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task AgentAttestation_WaitsForConcurrentDisableAndIsDenied()
    {
        var org = await SeedAsync();
        var member = "user_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var setup = postgres.CreateContext())
            {
                var organization = await setup.Organizations.SingleAsync(x => x.Id == org);
                organization.ActivatedAtUtc = DateTimeOffset.UtcNow;
                setup.Users.Add(new User { Id = member, DisplayName = member });
                setup.OrganizationMemberships.Add(
                    new OrganizationMembership
                    {
                        Id = Guid.NewGuid().ToString(),
                        OrganizationId = org,
                        UserId = member,
                        CreatedByUserId = org,
                        Role = "member",
                        Status = MembershipStatus.Active,
                    }
                );
                await setup.SaveChangesAsync();
            }
            await using var db = postgres.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var config =
                "{\"MaxConcurrentReviews\":4,\"ExecutionLeaseDuration\":\"00:02:00\",\"AllowAgentAttestedTriage\":false}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE zeeq.core_organizations SET code_review_configuration = {config}::jsonb WHERE id = {org}"
            );
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var write = Set(
                Snapshot(org),
                CodeReviewFindingTriageChannel.AgentAttested,
                "reason"
            ) with
            {
                ActorUserId = member,
                ConfirmationQuote = "yes",
            };
            var pending = store.UpsertLwwAsync(write);
            var first = await Task.WhenAny(pending, Task.Delay(300));
            await Assert.That(first == pending).IsFalse();
            await tx.CommitAsync();
            var result = await pending;
            await Assert.That(result.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
        }
        finally
        {
            await using (var cleanup = postgres.CreateContext())
            {
                await cleanup
                    .OrganizationMemberships.Where(x => x.OrganizationId == org)
                    .ExecuteDeleteAsync();
                await cleanup.Users.Where(x => x.Id == member).ExecuteDeleteAsync();
            }
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task ConcurrentInsertAndHumanDecision_CannotBeOverwrittenByMcp()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var results = await Task.WhenAll(
                store.UpsertLwwAsync(
                    Set(Snapshot(org), CodeReviewFindingTriageChannel.Mcp, "agent")
                ),
                store.UpsertLwwAsync(Set(Snapshot(org), CodeReviewFindingTriageChannel.Ui, "human"))
            );
            var rows = await store.ListForReviewAsync(org, "review_1");
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert
                .That(rows[0].ApprovalState)
                .IsEqualTo(CodeReviewFindingApprovalState.Confirmed);
            await Assert.That(rows[0].CurrentReason).IsEqualTo("human");
            await Assert.That(results[1].Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await Assert
                .That(
                    results[0].Status
                        is FindingTriageWriteStatus.Applied
                            or FindingTriageWriteStatus.Forbidden
                )
                .IsTrue();
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task ClearWatermarkAndEmbeddingLease_PreventStaleWrites()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            var created = await store.UpsertLwwAsync(
                Set(snapshot, CodeReviewFindingTriageChannel.Ui, "reason")
            );
            var claims = await store.ClaimPendingEmbeddingsAsync(
                "model",
                TimeSpan.FromMinutes(1),
                10
            );
            var claim = claims.Single(x => x.Row.OrganizationId == org);
            var clear = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Clear,
                    null,
                    null,
                    CodeReviewFindingTriageChannel.Ui,
                    "user"
                )
            );
            var old = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Set,
                    CodeReviewFindingDisposition.Deferred,
                    "late",
                    CodeReviewFindingTriageChannel.GitHub,
                    "user",
                    created.Row!.UpdatedAtUtc
                )
            );
            var vector = new HalfVector(Enumerable.Repeat((Half)1, 768).ToArray());
            await Assert.That(clear.Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await Assert.That(old.Status).IsEqualTo(FindingTriageWriteStatus.IgnoredStale);
            await Assert
                .That(
                    await store.TryCompleteEmbeddingAsync(
                        org,
                        claim.Row.Id,
                        claim.UpdatedAtUtc,
                        claim.StartedAtUtc,
                        vector,
                        "model"
                    )
                )
                .IsFalse();
            await Assert
                .That(
                    await store.ReleaseEmbeddingClaimAsync(
                        org,
                        claim.Row.Id,
                        claim.UpdatedAtUtc,
                        claim.StartedAtUtc
                    )
                )
                .IsFalse();
            await Assert.That(clear.Row!.Disposition).IsNull();
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task ReclaimedLease_RejectsOldWorker_AndSearchUsesRawThreshold()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            await store.UpsertLwwAsync(Set(snapshot, CodeReviewFindingTriageChannel.Ui, "reason"));
            var first = (
                await store.ClaimPendingEmbeddingsAsync("model", TimeSpan.FromMinutes(1), 100)
            ).Single(x => x.Row.OrganizationId == org);
            await using (var db = postgres.CreateContext())
            {
                await db
                    .CodeReviewFindingTriages.Where(x => x.OrganizationId == org)
                    .ExecuteUpdateAsync(x =>
                        x.SetProperty(y => y.EmbeddingStartedAt, DateTimeOffset.UtcNow.AddHours(-1))
                    );
            }
            var second = (
                await store.ClaimPendingEmbeddingsAsync("model", TimeSpan.FromMinutes(1), 100)
            ).Single(x => x.Row.OrganizationId == org);
            var vector = new HalfVector(Enumerable.Repeat((Half)1, 768).ToArray());
            await Assert
                .That(
                    await store.TryCompleteEmbeddingAsync(
                        org,
                        first.Row.Id,
                        first.UpdatedAtUtc,
                        first.StartedAtUtc,
                        vector,
                        "model"
                    )
                )
                .IsFalse();
            await Assert
                .That(
                    await store.ReleaseEmbeddingClaimAsync(
                        org,
                        first.Row.Id,
                        first.UpdatedAtUtc,
                        first.StartedAtUtc
                    )
                )
                .IsFalse();
            await Assert
                .That(
                    await store.TryCompleteEmbeddingAsync(
                        org,
                        second.Row.Id,
                        second.UpdatedAtUtc,
                        second.StartedAtUtc,
                        vector,
                        "model"
                    )
                )
                .IsTrue();
            var matches = await store.SearchConfirmedFalsePositivesAsync(
                org,
                null,
                "owner/repo",
                vector,
                "model",
                0.99,
                10
            );
            await Assert.That(matches.Count).IsEqualTo(1);
            var opposite = new HalfVector(
                vector.ToArray().Select(x => (Half)(-(float)x)).ToArray()
            );
            var belowThreshold = await store.SearchConfirmedFalsePositivesAsync(
                org,
                null,
                "owner/repo",
                opposite,
                "model",
                0.99,
                10
            );
            await Assert.That(belowThreshold.Count).IsEqualTo(0);
            var afterMapping = await store.SearchConfirmedFalsePositivesAsync(
                org,
                "new-repository-id",
                "owner/repo",
                vector,
                "model",
                0.99,
                10
            );
            await Assert.That(afterMapping.Count).IsEqualTo(1);
            var excluded = await store.SearchConfirmedFalsePositivesAsync(
                org,
                null,
                "owner/repo",
                vector,
                "model",
                0.99,
                10,
                activeReviewId: "review_1"
            );
            await Assert.That(excluded.Count).IsEqualTo(0);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task SearchAsOfExcludesSamePullRequestAndConfirmationsAtOrAfterCutoff()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
            for (var i = 1; i <= 3; i++)
            {
                var row = Snapshot(org, $"review_{i}", i);
                await store.UpsertLwwAsync(Set(row, CodeReviewFindingTriageChannel.Ui, "reason"));
            }
            await using (var db = postgres.CreateContext())
            {
                var rows = await db
                    .CodeReviewFindingTriages.Where(x => x.OrganizationId == org)
                    .ToListAsync();
                foreach (var row in rows)
                {
                    row.ConfirmedAtUtc =
                        row.PullRequestNumber == 3 ? cutoff : cutoff.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }
            var vector = new HalfVector(Enumerable.Repeat((Half)1, 768).ToArray());
            var claims = await store.ClaimPendingEmbeddingsAsync(
                "model",
                TimeSpan.FromMinutes(1),
                100
            );
            foreach (var claim in claims.Where(x => x.Row.OrganizationId == org))
                await store.TryCompleteEmbeddingAsync(
                    org,
                    claim.Row.Id,
                    claim.UpdatedAtUtc,
                    claim.StartedAtUtc,
                    vector,
                    "model"
                );
            var results = await store.SearchConfirmedFalsePositivesAsync(
                org,
                null,
                "owner/repo",
                vector,
                "model",
                0.9,
                10,
                activePrNumber: 2,
                asOfUtc: cutoff
            );
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results.Single().Row.PullRequestNumber).IsEqualTo(1);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task WaitingMcpWrite_ObservesHumanConfirmationAfterLockReleased()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            await store.UpsertLwwAsync(
                Set(snapshot, CodeReviewFindingTriageChannel.Mcp, "proposal")
            );
            await using var db = postgres.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var locked = await db
                .CodeReviewFindingTriages.FromSqlInterpolated(
                    $"SELECT * FROM zeeq.code_review_finding_triages WHERE organization_id = {org} FOR UPDATE"
                )
                .SingleAsync();
            var pending = store.UpsertLwwAsync(
                Set(Snapshot(org), CodeReviewFindingTriageChannel.Mcp, "replacement")
            );
            await Task.Delay(150);
            await Assert.That(pending.IsCompleted).IsFalse();
            locked.ApprovalState = CodeReviewFindingApprovalState.Confirmed;
            locked.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            locked.ConfirmedByUserId = "human";
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            var result = await pending;
            await Assert.That(result.Status).IsEqualTo(FindingTriageWriteStatus.Forbidden);
            await Assert.That(result.Row!.CurrentReason).IsEqualTo("proposal");
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task ClearTombstoneAndIdenticalRetry_PreserveWatermarkAndTimestamp()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            var clear = new FindingTriageWrite(
                snapshot,
                FindingTriageOperation.Clear,
                null,
                null,
                CodeReviewFindingTriageChannel.Ui,
                "human"
            );
            var first = await store.UpsertLwwAsync(clear);
            var retry = await store.UpsertLwwAsync(clear);
            await Assert.That(retry.Row!.UpdatedAtUtc).IsEqualTo(first.Row!.UpdatedAtUtc);
            var late = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Set,
                    CodeReviewFindingDisposition.FalsePositive,
                    "late",
                    CodeReviewFindingTriageChannel.GitHub,
                    "human",
                    first.Row.LastSourceEventAtUtc.AddSeconds(-1)
                )
            );
            await Assert.That(late.Status).IsEqualTo(FindingTriageWriteStatus.IgnoredStale);
            await Assert.That((await store.ListForReviewAsync(org, "review_1")).Count).IsEqualTo(1);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task NewerIdenticalGithubConfirmation_AdvancesWatermarkWithoutChangingSnapshot()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            await store.UpsertLwwAsync(Set(snapshot, CodeReviewFindingTriageChannel.Mcp, "reason"));
            var t10 = DateTimeOffset
                .FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .AddMinutes(1);
            FindingTriageWrite Confirm(DateTimeOffset at) =>
                new(
                    snapshot,
                    FindingTriageOperation.Confirm,
                    null,
                    null,
                    CodeReviewFindingTriageChannel.GitHub,
                    "human",
                    at
                );
            var first = await store.UpsertLwwAsync(Confirm(t10));
            var vector = new HalfVector(Enumerable.Repeat((Half)1, 768).ToArray());
            var started = t10.AddMinutes(-2);
            await using (var db = postgres.CreateContext())
            {
                var row = await db.CodeReviewFindingTriages.SingleAsync(x =>
                    x.OrganizationId == org
                );
                row.Embedding = vector;
                row.EmbeddingModel = "model";
                row.EmbeddingStartedAt = started;
                await db.SaveChangesAsync();
            }
            var t20 = t10.AddSeconds(10);
            var retry = await store.UpsertLwwAsync(Confirm(t20));
            await Assert.That(retry.Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await using (var db = postgres.CreateContext())
            {
                var row = await db
                    .CodeReviewFindingTriages.AsNoTracking()
                    .SingleAsync(x => x.OrganizationId == org);
                await Assert.That(row.LastSourceEventAtUtc).IsEqualTo(t20);
                await Assert.That(row.UpdatedAtUtc).IsEqualTo(first.Row!.UpdatedAtUtc);
                await Assert.That(row.Embedding!.ToString()).IsEqualTo(vector.ToString());
                await Assert.That(row.EmbeddingModel).IsEqualTo("model");
                await Assert.That(row.EmbeddingStartedAt).IsEqualTo(started);
            }
            var delayed = await store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Clear,
                    null,
                    null,
                    CodeReviewFindingTriageChannel.GitHub,
                    "human",
                    t10.AddSeconds(5)
                )
            );
            await Assert.That(delayed.Status).IsEqualTo(FindingTriageWriteStatus.IgnoredStale);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task DatabaseChecks_RejectInvalidStateAndCrossTenantRepository()
    {
        var org = await SeedAsync();
        try
        {
            await using var db = postgres.CreateContext();
            var invalid = Snapshot(org);
            invalid.Disposition = CodeReviewFindingDisposition.FalsePositive;
            invalid.ApprovalState = CodeReviewFindingApprovalState.Open;
            db.CodeReviewFindingTriages.Add(invalid);
            await Assert.That(async () => await db.SaveChangesAsync()).Throws<DbUpdateException>();
            db.ChangeTracker.Clear();
            var badRepo = Snapshot(org);
            badRepo.RepositoryId = "missing-repository";
            db.CodeReviewFindingTriages.Add(badRepo);
            await Assert.That(async () => await db.SaveChangesAsync()).Throws<DbUpdateException>();
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task SearchExcludesOtherTenantsAndMappedRepositoriesWithReusedName()
    {
        var org = await SeedAsync();
        var otherOrg = await SeedAsync();
        try
        {
            var repoId = "repo_" + Guid.NewGuid().ToString("N");
            var otherRepoId = "repo_" + Guid.NewGuid().ToString("N");
            await using (var db = postgres.CreateContext())
            {
                db.CodeRepositories.AddRange(
                    new CodeRepository
                    {
                        Id = repoId,
                        OrganizationId = org,
                        Provider = "github",
                        OwnerQualifiedName = "owner/new",
                        DisplayName = "new",
                        ReviewConfiguration = new CodeRepositoryReviewConfiguration
                        {
                            CheckRun = new CodeRepositoryReviewCheckRunConfiguration(),
                        },
                    },
                    new CodeRepository
                    {
                        Id = otherRepoId,
                        OrganizationId = org,
                        Provider = "github",
                        OwnerQualifiedName = "owner/old",
                        DisplayName = "old",
                        ReviewConfiguration = new CodeRepositoryReviewConfiguration
                        {
                            CheckRun = new CodeRepositoryReviewCheckRunConfiguration(),
                        },
                    }
                );
                await db.SaveChangesAsync();
            }
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var mapped = Snapshot(org);
            mapped.RepositoryId = otherRepoId;
            await store.UpsertLwwAsync(Set(mapped, CodeReviewFindingTriageChannel.Ui, "reason"));
            await store.UpsertLwwAsync(
                Set(Snapshot(otherOrg), CodeReviewFindingTriageChannel.Ui, "reason")
            );
            var claims = await store.ClaimPendingEmbeddingsAsync(
                "model",
                TimeSpan.FromMinutes(1),
                100
            );
            var vector = new HalfVector(Enumerable.Repeat((Half)1, 768).ToArray());
            foreach (
                var claim in claims.Where(x =>
                    x.Row.OrganizationId == org || x.Row.OrganizationId == otherOrg
                )
            )
                await store.TryCompleteEmbeddingAsync(
                    claim.Row.OrganizationId,
                    claim.Row.Id,
                    claim.UpdatedAtUtc,
                    claim.StartedAtUtc,
                    vector,
                    "model"
                );
            var matches = await store.SearchConfirmedFalsePositivesAsync(
                org,
                repoId,
                "owner/repo",
                vector,
                "model",
                0.9,
                10
            );
            await Assert.That(matches.Count).IsEqualTo(0);
            await Assert
                .That(async () =>
                    await store.SearchConfirmedFalsePositivesAsync(
                        org,
                        repoId,
                        "owner/repo",
                        new HalfVector(new Half[768]),
                        "model",
                        0.9,
                        10
                    )
                )
                .Throws<ArgumentException>();
        }
        finally
        {
            await using (var db = postgres.CreateContext())
            {
                await db
                    .CodeReviewFindingTriages.Where(x => x.OrganizationId == org)
                    .ExecuteDeleteAsync();
                await db.CodeRepositories.Where(x => x.OrganizationId == org).ExecuteDeleteAsync();
            }
            await CleanupAsync(org);
            await CleanupAsync(otherOrg);
        }
    }

    [Test]
    public async Task OlderGithubEvent_WaitingOnClear_IsIgnored()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            var snapshot = Snapshot(org);
            var initial = await store.UpsertLwwAsync(
                Set(snapshot, CodeReviewFindingTriageChannel.Ui, "reason")
            );
            await using var db = postgres.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var locked = await db
                .CodeReviewFindingTriages.FromSqlInterpolated(
                    $"SELECT * FROM zeeq.code_review_finding_triages WHERE organization_id = {org} FOR UPDATE"
                )
                .SingleAsync();
            var pending = store.UpsertLwwAsync(
                new(
                    snapshot,
                    FindingTriageOperation.Set,
                    CodeReviewFindingDisposition.Deferred,
                    "old event",
                    CodeReviewFindingTriageChannel.GitHub,
                    "user",
                    initial.Row!.LastSourceEventAtUtc.AddSeconds(-1)
                )
            );
            await Task.Delay(150);
            await Assert.That(pending.IsCompleted).IsFalse();
            locked.Disposition = null;
            locked.ApprovalState = CodeReviewFindingApprovalState.Open;
            locked.CurrentReason = null;
            locked.ConfirmedByUserId = null;
            locked.ConfirmedAtUtc = null;
            locked.ClearedByUserId = "human";
            locked.ClearedAtUtc = DateTimeOffset.UtcNow;
            locked.LastSourceEventAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            var result = await pending;
            await Assert.That(result.Status).IsEqualTo(FindingTriageWriteStatus.IgnoredStale);
            await Assert.That(result.Row!.Disposition).IsNull();
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [Test]
    public async Task ConcurrentUncommittedInsert_RetriesOccurrenceUniqueViolation()
    {
        var org = await SeedAsync();
        try
        {
            var store = new PostgresCodeReviewFindingTriageStore(new Factory(postgres));
            await using var db = postgres.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var row = Snapshot(org);
            row.LastSourceEventAtUtc = DateTimeOffset.UtcNow;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.CodeReviewFindingTriages.Add(row);
            await db.SaveChangesAsync();
            var pending = store.UpsertLwwAsync(
                Set(Snapshot(org), CodeReviewFindingTriageChannel.Ui, "human")
            );
            await Task.Delay(150);
            await Assert.That(pending.IsCompleted).IsFalse();
            await tx.CommitAsync();
            var result = await pending;
            await Assert.That(result.Status).IsEqualTo(FindingTriageWriteStatus.Applied);
            await Assert.That(result.Row!.Id).IsEqualTo(row.Id);
            await Assert.That(result.Row.CurrentReason).IsEqualTo("human");
            await Assert.That((await store.ListForReviewAsync(org, "review_1")).Count).IsEqualTo(1);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }
}
