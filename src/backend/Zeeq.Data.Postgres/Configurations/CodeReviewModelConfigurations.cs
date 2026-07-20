using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for GitHub App installation state.
/// </summary>
/// <remarks>
/// This table is the bridge between a Zeeq organization/team and a GitHub
/// App installation ID. GitHub API clients resolve installation tokens through
/// this row; repository rows intentionally do not own installation IDs so
/// installation lifecycle changes can be handled independently.
/// </remarks>
internal sealed class GitHubAppInstallationConfiguration
    : IEntityTypeConfiguration<GitHubAppInstallation>
{
    public void Configure(EntityTypeBuilder<GitHubAppInstallation> entity)
    {
        entity.ToTable("code_review_github_app_installations");
        entity.HasKey(installation => installation.Id);

        entity.Property(installation => installation.Id).HasMaxLength(128);
        entity.Property(installation => installation.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(installation => installation.TeamId).HasMaxLength(128);
        entity.Property(installation => installation.AccountLogin).IsRequired().HasMaxLength(256);
        entity.Property(installation => installation.AccountType).IsRequired().HasMaxLength(64);
        entity
            .Property(installation => installation.RepositorySelection)
            .IsRequired()
            .HasMaxLength(32);
        entity.Property(installation => installation.RawInstallationJson).HasColumnType("jsonb");
        entity.Property(installation => installation.CreatedAtUtc).IsRequired();
        entity.Property(installation => installation.UpdatedAtUtc).IsRequired();
        entity.Property(installation => installation.InstalledAtUtc).IsRequired();

        // GitHub installation IDs are global and must never be linked to two
        // different Zeeq organizations.
        entity.HasIndex(installation => installation.InstallationId).IsUnique();
        entity.HasIndex(installation => new
        {
            installation.OrganizationId,
            installation.TeamId,
            installation.DisabledAtUtc,
        });
        entity.HasIndex(installation => new
        {
            installation.AccountLogin,
            installation.AccountType,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(installation => installation.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(installation => new { installation.OrganizationId, installation.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for repositories enabled for Zeeq code review workflows.
/// </summary>
/// <remarks>
/// Repository mappings are the webhook gate. If a GitHub webhook cannot resolve
/// an enabled active row here, Zeeq should acknowledge the delivery and do no
/// queue work. Disabled rows are retained for history and future audit trails.
/// </remarks>
internal sealed class CodeRepositoryConfiguration : IEntityTypeConfiguration<CodeRepository>
{
    public void Configure(EntityTypeBuilder<CodeRepository> entity)
    {
        entity.ToTable("code_review_repositories");
        entity.HasKey(repository => repository.Id);

        entity.Property(repository => repository.Id).HasMaxLength(128);
        entity.Property(repository => repository.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(repository => repository.TeamId).HasMaxLength(128);
        entity.Property(repository => repository.Provider).IsRequired().HasMaxLength(64);
        entity.Property(repository => repository.OwnerQualifiedName).IsRequired().HasMaxLength(512);
        entity.Property(repository => repository.DisplayName).IsRequired().HasMaxLength(256);
        entity.Property(repository => repository.VisibleInLibraryPicker).HasDefaultValue(true);
        entity.Property(repository => repository.LibraryIds).HasColumnType("text[]");
        entity.OwnsOne(
            repository => repository.ReviewConfiguration,
            configuration =>
            {
                configuration.ToJson("configuration_json");
                configuration.OwnsOne(
                    review => review.FileFilter,
                    fileFilter =>
                    {
                        ConfigureFileFilter(fileFilter);
                    }
                );
                configuration.OwnsOne(
                    review => review.CheckRun,
                    checkRun =>
                    {
                        checkRun.Property(c => c.BlockOnCritical);
                        checkRun.Property(c => c.BlockOnMajor);
                    }
                );
            }
        );
        entity.Property(repository => repository.CreatedAtUtc).IsRequired();
        entity.Property(repository => repository.UpdatedAtUtc).IsRequired();

        // One active mapping per organization/provider/repository. The filter
        // allows historical disabled rows to remain after a repository is removed.
        entity
            .HasIndex(repository => new
            {
                repository.OrganizationId,
                repository.Provider,
                repository.OwnerQualifiedName,
            })
            .IsUnique()
            .HasFilter("disabled_at_utc IS NULL");
        // Webhook ingress starts from provider + owner/name before it knows the
        // Zeeq organization, so this index supports that resolution path.
        entity.HasIndex(repository => new
        {
            repository.Provider,
            repository.OwnerQualifiedName,
            repository.DisabledAtUtc,
        });
        // Organization settings screens list configured repository mappings by org/team,
        // including paused rows where Enabled is false.
        entity.HasIndex(repository => new
        {
            repository.OrganizationId,
            repository.TeamId,
            repository.DisabledAtUtc,
        });

        entity
            .HasIndex(repository => repository.LibraryIds)
            .HasMethod("GIN")
            .HasDatabaseName("ix_code_repository_library_ids");

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(repository => repository.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(repository => new { repository.OrganizationId, repository.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureFileFilter(
        OwnedNavigationBuilder<CodeRepositoryReviewConfiguration, CodeReviewFileFilter> fileFilter
    )
    {
        fileFilter.OwnsMany(
            filter => filter.IncludedFiles,
            criteria =>
            {
                criteria.Property(item => item.Pattern).HasMaxLength(1024);
                criteria.Property(item => item.MatchType).HasConversion<string>().HasMaxLength(32);
            }
        );
        fileFilter.OwnsMany(
            filter => filter.ExcludedFiles,
            criteria =>
            {
                criteria.Property(item => item.Pattern).HasMaxLength(1024);
                criteria.Property(item => item.MatchType).HasConversion<string>().HasMaxLength(32);
            }
        );
    }
}

/// <summary>
/// EF mapping for durable organization execution-capacity leases.
/// </summary>
internal sealed class CodeReviewExecutionLeaseConfiguration
    : IEntityTypeConfiguration<CodeReviewExecutionLease>
{
    public void Configure(EntityTypeBuilder<CodeReviewExecutionLease> entity)
    {
        entity.ToTable("code_review_execution_leases");
        entity.HasKey(lease => new { lease.OrganizationId, lease.SlotIndex });

        entity.Property(lease => lease.Id).IsRequired().HasMaxLength(128);
        entity.Property(lease => lease.OrganizationId).HasMaxLength(128);
        entity.Property(lease => lease.TeamId).HasMaxLength(128);
        entity.Property(lease => lease.LeaseId).IsRequired().HasMaxLength(128);
        entity.Property(lease => lease.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(lease => lease.PullRequestRecordId).IsRequired().HasMaxLength(128);
        entity.Property(lease => lease.PullRequestCreatedAtUtc).IsRequired();
        entity.Property(lease => lease.CodeReviewRecordId).IsRequired().HasMaxLength(128);
        entity.Property(lease => lease.CodeReviewCreatedAtUtc).IsRequired();
        entity.Property(lease => lease.AcquiredAtUtc).IsRequired();
        entity.Property(lease => lease.RenewedAtUtc).IsRequired();
        entity.Property(lease => lease.ExpiresAtUtc).IsRequired();
        entity.Property(lease => lease.WorkerId).HasMaxLength(256);
        entity.Property(lease => lease.CreatedAtUtc).IsRequired();
        entity.Property(lease => lease.UpdatedAtUtc).IsRequired();

        entity.HasIndex(lease => lease.LeaseId).IsUnique();
        entity.HasIndex(lease => lease.CodeReviewRecordId).IsUnique();
        entity.HasIndex(lease => new { lease.OrganizationId, lease.ExpiresAtUtc });
        entity.HasIndex(lease => new { lease.OrganizationId, lease.RenewedAtUtc });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(lease => lease.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasForeignKey(lease => lease.RepositoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for repository-scoped reviewer agent configuration.
/// </summary>
internal sealed class CodeReviewerAgentConfiguration : IEntityTypeConfiguration<CodeReviewerAgent>
{
    public void Configure(EntityTypeBuilder<CodeReviewerAgent> entity)
    {
        entity.ToTable("code_reviewer_agents");
        entity.HasKey(agent => agent.Id);

        entity.Property(agent => agent.Id).HasMaxLength(128);
        entity.Property(agent => agent.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(agent => agent.TeamId).HasMaxLength(128);
        entity.Property(agent => agent.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(agent => agent.DisplayName).IsRequired().HasMaxLength(256);
        entity.Property(agent => agent.ReviewFacet).IsRequired().HasMaxLength(128);
        entity
            .Property(agent => agent.ModelTier)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(agent => agent.Prompt).IsRequired();
        entity.Property(agent => agent.CreatedAtUtc).IsRequired();
        entity.Property(agent => agent.UpdatedAtUtc).IsRequired();

        entity.OwnsOne(
            agent => agent.ActivationConfiguration,
            activation =>
            {
                activation.ToJson("activation_configuration");
                activation.OwnsMany(
                    configuration => configuration.IncludedFiles,
                    criteria =>
                    {
                        criteria.Property(item => item.Pattern).HasMaxLength(1024);
                        criteria
                            .Property(item => item.MatchType)
                            .HasConversion<string>()
                            .HasMaxLength(32);
                    }
                );
                activation.OwnsMany(
                    configuration => configuration.ExcludedFiles,
                    criteria =>
                    {
                        criteria.Property(item => item.Pattern).HasMaxLength(1024);
                        criteria
                            .Property(item => item.MatchType)
                            .HasConversion<string>()
                            .HasMaxLength(32);
                    }
                );
            }
        );

        entity.HasIndex(agent => new
        {
            agent.OrganizationId,
            agent.RepositoryId,
            agent.Enabled,
            agent.DisabledAtUtc,
        });
        entity.HasIndex(agent => new
        {
            agent.OrganizationId,
            agent.TeamId,
            agent.RepositoryId,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(agent => agent.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(agent => new { agent.OrganizationId, agent.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasForeignKey(agent => agent.RepositoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for partitioned pull request stream records.
/// </summary>
/// <remarks>
/// Pull request records are optimized for the high-read recent PR stream and
/// detail views. The primary key includes <c>CreatedAtUtc</c> because the table
/// is partitioned by that timestamp; cross-partition provider uniqueness lives
/// in <see cref="PullRequestLookup" />.
/// </remarks>
internal sealed class PullRequestRecordConfiguration : IEntityTypeConfiguration<PullRequestRecord>
{
    public void Configure(EntityTypeBuilder<PullRequestRecord> entity)
    {
        entity.ToTable("code_review_pull_request_records");
        entity.HasKey(record => new { record.Id, record.CreatedAtUtc });

        entity.Property(record => record.Id).HasMaxLength(128);
        entity.Property(record => record.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(record => record.TeamId).HasMaxLength(128);
        entity.Property(record => record.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(record => record.OwnerQualifiedRepoName).IsRequired().HasMaxLength(512);
        entity.Property(record => record.GitHubNodeId).IsRequired().HasMaxLength(256);
        entity.Property(record => record.Title).IsRequired().HasMaxLength(1024);
        entity.Property(record => record.Branch).IsRequired().HasMaxLength(512);
        entity.Property(record => record.BaseBranch).IsRequired().HasMaxLength(512);
        entity.Property(record => record.HeadSha).IsRequired().HasMaxLength(128);
        entity.Property(record => record.AuthorLogin).IsRequired().HasMaxLength(256);
        entity.Property(record => record.HtmlUrl).IsRequired().HasMaxLength(2048);
        entity
            .Property(record => record.State)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity
            .Property(record => record.ClaimStatus)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(record => record.ClaimedByUserId).HasMaxLength(128);
        entity.Property(record => record.FeatureId).HasMaxLength(128);
        entity.Property(record => record.TagsJson).HasColumnType("jsonb");
        entity.Property(record => record.LabelsJson).HasColumnType("jsonb");

        // NOTE: CheckRunState is a nullable navigation property mapped as an owned
        // JSON document. When CheckRunState is null on the model (no check has been
        // posted), EF Core writes NULL for the jsonb column. The OwnsOne shape is
        // intentional — it allows EF to materialize the JSON into the typed
        // PullRequestCheckRunState when present, and to treat it as absent when null.
        // Regression tests in CodeReviewStoreIntegrationTests verify the round-trip
        // for null, Blocking, and Removed states.
        entity.OwnsOne(
            record => record.CheckRunState,
            state =>
            {
                state.ToJson("check_run_state");
                state.Property(s => s.CheckRunId).IsRequired();
                state.Property(s => s.HeadSha).IsRequired().HasMaxLength(128);
                state.Property(s => s.State).IsRequired().HasMaxLength(32).HasConversion<string>(); // NOTE: stored as string name in JSONB for readability and ordinal stability
                state.Property(s => s.RemovedBy).HasMaxLength(256);
                state.Property(s => s.RemovedAtUtc);
            }
        );
        entity.Property(record => record.CreatedAtUtc).IsRequired();
        entity.Property(record => record.UpdatedAtUtc).IsRequired();

        // Main recent-stream index for org/team views. CreatedAtUtc plus ID
        // matches the cursor boundary used by the stores.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.TeamId,
            record.CreatedAtUtc,
            record.Id,
        });
        // Supports queue/workflow screens that filter unclaimed or claimed PRs.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.ClaimStatus,
            record.CreatedAtUtc,
            record.Id,
        });
        // Repository/PR number lookup is intentionally not unique here because
        // uniqueness across partitions is enforced by PullRequestLookup.
        entity.HasIndex(record => new
        {
            record.RepositoryId,
            record.PullRequestNumber,
            record.CreatedAtUtc,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(record => record.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasForeignKey(record => record.RepositoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for the non-partitioned provider PR lookup table.
/// </summary>
/// <remarks>
/// This table is the compact cross-partition guard for one provider pull request
/// identity. It stores the partitioned row ID plus <c>PullRequestCreatedAtUtc</c>
/// so handlers can jump directly to the current PR record without scanning
/// date partitions.
/// </remarks>
internal sealed class PullRequestLookupConfiguration : IEntityTypeConfiguration<PullRequestLookup>
{
    public void Configure(EntityTypeBuilder<PullRequestLookup> entity)
    {
        entity.ToTable("code_review_pull_request_lookups");
        entity.HasKey(lookup => new
        {
            lookup.OrganizationId,
            lookup.RepositoryId,
            lookup.PullRequestNumber,
        });

        entity.Property(lookup => lookup.OrganizationId).HasMaxLength(128);
        entity.Property(lookup => lookup.TeamId).HasMaxLength(128);
        entity.Property(lookup => lookup.RepositoryId).HasMaxLength(128);
        entity.Property(lookup => lookup.OwnerQualifiedRepoName).IsRequired().HasMaxLength(512);
        entity.Property(lookup => lookup.PullRequestRecordId).IsRequired().HasMaxLength(128);
        entity.Property(lookup => lookup.PullRequestCreatedAtUtc).IsRequired();
        entity.Property(lookup => lookup.UpdatedAtUtc).IsRequired();

        // A partitioned PR record should be the current target of at most one
        // provider identity lookup.
        entity.HasIndex(lookup => lookup.PullRequestRecordId).IsUnique();
        // Supports settings/debug views that show recently changed PR mappings.
        entity.HasIndex(lookup => new
        {
            lookup.OrganizationId,
            lookup.TeamId,
            lookup.UpdatedAtUtc,
        });
    }
}

/// <summary>
/// EF mapping for partitioned code review execution records.
/// </summary>
/// <remarks>
/// Each row is one review execution attempt. The table is partitioned by
/// <c>CreatedAtUtc</c> for cheap recent reads and retention management. It does
/// not enforce the one-active-review rule; <see cref="ActiveCodeReviewLock" />
/// owns that invariant while review records keep the durable execution history.
/// </remarks>
internal sealed class CodeReviewRecordConfiguration : IEntityTypeConfiguration<CodeReviewRecord>
{
    public void Configure(EntityTypeBuilder<CodeReviewRecord> entity)
    {
        entity.ToTable("code_review_records");
        entity.HasKey(record => new { record.Id, record.CreatedAtUtc });

        entity.Property(record => record.Id).HasMaxLength(128);
        entity.Property(record => record.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(record => record.TeamId).HasMaxLength(128);
        entity.Property(record => record.PullRequestRecordId).HasMaxLength(128);
        entity.Property(record => record.RepositoryId).HasMaxLength(128);
        entity.Property(record => record.AgentSessionId).HasMaxLength(128);
        entity.Property(record => record.OwnerQualifiedRepoName).IsRequired().HasMaxLength(512);
        entity.Property(record => record.Branch).IsRequired().HasMaxLength(512);
        entity.Property(record => record.Title).IsRequired().HasMaxLength(1024);
        entity.Property(record => record.AuthorLogin).IsRequired().HasMaxLength(256);
        entity
            .Property(record => record.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity
            .Property(record => record.RequestOrigin)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(record => record.ReviewGroupId).HasMaxLength(128);
        entity.Property(record => record.PreviousReviewId).HasMaxLength(128);
        entity.Property(record => record.ExecutionTraceParent).HasMaxLength(128);
        entity.Property(record => record.ExecutionTraceState).HasMaxLength(512);
        entity
            .Property(record => record.SourceTelemetryPayload)
            .HasColumnName("source_telemetry_payload")
            .HasColumnType("jsonb");
        entity.Property(record => record.FindingsStorageUri).HasMaxLength(2048);
        entity.Property(record => record.FailureMessage).HasMaxLength(4096);
        entity.Property(record => record.CreatedAtUtc).IsRequired();
        entity.Property(record => record.UpdatedAtUtc).IsRequired();

        // Main recent-stream index for org/team code review lists.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.TeamId,
            record.CreatedAtUtc,
        });
        // Lets workflow handlers find recent or active attempts for one PR.
        entity.HasIndex(record => new { record.PullRequestRecordId, record.Status });
        // Helps distinguish webhook-originated review work from manual or agent requests.
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.RequestOrigin,
            record.CreatedAtUtc,
        });

        // Dashboard review aggregates by repository over a window (UI-3/UI-4 "by repo").
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.RepositoryId,
            record.CreatedAtUtc,
        });

        // Dashboard review aggregates by author over a window (UI-3/UI-4 "by user").
        entity.HasIndex(record => new
        {
            record.OrganizationId,
            record.AuthorLogin,
            record.CreatedAtUtc,
        });

        // Previous-review lookup for agent runs. Partial index keeps it cheap
        // and PR reviews (null session) out of it.
        entity
            .HasIndex(record => new
            {
                record.OrganizationId,
                record.AgentSessionId,
                record.CreatedAtUtc,
            })
            .HasFilter("agent_session_id IS NOT NULL");

        // Finds the immediate completed predecessor for a review-group follow-up without
        // scanning unrelated review history. Status precedes CreatedAtUtc to preserve its
        // equality predicate before the newest-first ordering.
        entity
            .HasIndex(record => new
            {
                record.OrganizationId,
                record.ReviewGroupId,
                record.Status,
                record.CreatedAtUtc,
            })
            .HasFilter("review_group_id IS NOT NULL");
    }
}

/// <summary>
/// EF mapping for the active-review guard table.
/// </summary>
/// <remarks>
/// This non-partitioned table owns the durable "one active review per pull
/// request" invariant. It points back to partitioned PR/review rows using their
/// IDs and timestamps, and is removed when the active review reaches a terminal
/// state.
/// </remarks>
internal sealed class ActiveCodeReviewLockConfiguration
    : IEntityTypeConfiguration<ActiveCodeReviewLock>
{
    public void Configure(EntityTypeBuilder<ActiveCodeReviewLock> entity)
    {
        entity.ToTable("code_review_active_locks");
        entity.HasKey(activeLock => new
        {
            activeLock.OrganizationId,
            activeLock.PullRequestRecordId,
        });

        entity.Property(activeLock => activeLock.OrganizationId).HasMaxLength(128);
        entity.Property(activeLock => activeLock.TeamId).HasMaxLength(128);
        entity.Property(activeLock => activeLock.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(activeLock => activeLock.PullRequestRecordId).HasMaxLength(128);
        entity.Property(activeLock => activeLock.PullRequestCreatedAtUtc).IsRequired();
        entity.Property(activeLock => activeLock.CodeReviewRecordId).IsRequired().HasMaxLength(128);
        entity.Property(activeLock => activeLock.CodeReviewCreatedAtUtc).IsRequired();
        entity
            .Property(activeLock => activeLock.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(activeLock => activeLock.AcquiredAtUtc).IsRequired();
        entity.Property(activeLock => activeLock.ExpiresAtUtc).IsRequired();
        entity.Property(activeLock => activeLock.UpdatedAtUtc).IsRequired();

        // A review execution can be the active review for at most one PR.
        entity.HasIndex(activeLock => activeLock.CodeReviewRecordId).IsUnique();
        // Supports org/team operational views for active or stuck reviews.
        entity.HasIndex(activeLock => new
        {
            activeLock.OrganizationId,
            activeLock.TeamId,
            activeLock.UpdatedAtUtc,
        });
        entity.HasIndex(activeLock => activeLock.ExpiresAtUtc);
    }
}

/// <summary>
/// EF mapping for local Postgres-backed object storage.
/// </summary>
internal sealed class PostgresStorageObjectConfiguration
    : IEntityTypeConfiguration<PostgresStorageObject>
{
    public void Configure(EntityTypeBuilder<PostgresStorageObject> entity)
    {
        entity.ToTable("storage_objects");
        entity.HasKey(storageObject => storageObject.Uri);

        entity.Property(storageObject => storageObject.Uri).HasMaxLength(2048);
        entity
            .Property(storageObject => storageObject.OrganizationId)
            .IsRequired()
            .HasMaxLength(128);
        entity.Property(storageObject => storageObject.Container).IsRequired().HasMaxLength(128);
        entity.Property(storageObject => storageObject.Path).IsRequired();
        entity.Property(storageObject => storageObject.ContentType).IsRequired().HasMaxLength(256);
        entity.Property(storageObject => storageObject.ContentText);
        entity.Property(storageObject => storageObject.ContentBytes);
        entity
            .Property(storageObject => storageObject.MetadataJson)
            .IsRequired()
            .HasColumnType("jsonb");
        entity.Property(storageObject => storageObject.CreatedAtUtc).IsRequired();
        entity.Property(storageObject => storageObject.UpdatedAtUtc).IsRequired();

        entity.HasIndex(storageObject => new
        {
            storageObject.OrganizationId,
            storageObject.Container,
            storageObject.CreatedAtUtc,
        });
        entity
            .HasIndex(storageObject => storageObject.ExpiresAtUtc)
            .HasFilter("expires_at_utc IS NOT NULL");
        entity
            .HasIndex(storageObject => new { storageObject.Container, storageObject.Path })
            .IsUnique();
    }
}

/// <summary>
/// EF mapping for GitHub webhook delivery idempotency claims.
/// </summary>
/// <remarks>
/// The delivery ID from GitHub is the primary key. Ingress claims this row
/// before publishing queue work so retries and duplicate deliveries can be
/// acknowledged without creating duplicate PR/review/comment work.
///
/// This table is claim infrastructure, not durable audit storage. The preferred
/// operational shape is an unlogged table with pg_cron deleting old claims in
/// small batches. That keeps WAL and retention overhead low for high-volume
/// webhook traffic while preserving a simple primary-key claim path. The
/// tradeoff is that an unclean Postgres restart may truncate claim rows and let
/// GitHub retry a delivery that Zeeq already processed.
///
/// This feature has not been deployed to production, so migrations may drop and
/// recreate the delivery-claim table instead of preserving old rows.
/// </remarks>
internal sealed class GitHubWebhookDeliveryConfiguration
    : IEntityTypeConfiguration<GitHubWebhookDelivery>
{
    public void Configure(EntityTypeBuilder<GitHubWebhookDelivery> entity)
    {
        entity.ToTable("code_review_github_webhook_delivery_claims");
        entity.IsUnlogged();
        entity.HasKey(delivery => delivery.DeliveryId);

        entity.Property(delivery => delivery.DeliveryId).HasMaxLength(128);
        entity.Property(delivery => delivery.ClaimedAtUtc).IsRequired();

        // Retention cleanup scans old claims by time; the hot path remains the
        // primary-key claim on the GitHub delivery id.
        entity.HasIndex(delivery => delivery.ClaimedAtUtc);
    }
}

/// <summary>
/// EF mapping for short-lived GitHub comment writer leases.
/// </summary>
/// <remarks>
/// This table is intentionally unlogged. Losing rows after an unclean Postgres
/// shutdown is acceptable because the lease only serializes in-flight comment
/// writes; the durable source of truth is the GitHub comment body plus the
/// authoritative render data referenced by the queued work. Future migrations
/// should preserve the unlogged setup unless the lease becomes durable business
/// state, which would require rethinking crash recovery and writer ownership.
/// </remarks>
internal sealed class GitHubCommentLeaseConfiguration : IEntityTypeConfiguration<GitHubCommentLease>
{
    public void Configure(EntityTypeBuilder<GitHubCommentLease> entity)
    {
        entity.ToTable("code_review_github_comment_leases");
        entity.IsUnlogged();
        entity.HasKey(lease => lease.LeaseKey);

        entity.Property(lease => lease.LeaseKey).HasMaxLength(512);
        entity.Property(lease => lease.WorkerId).IsRequired().HasMaxLength(256);
        entity.Property(lease => lease.AcquiredAtUtc).IsRequired();
        entity.Property(lease => lease.ExpiresAtUtc).IsRequired();

        // Maintenance and diagnostics can quickly find abandoned/expired rows.
        entity.HasIndex(lease => lease.ExpiresAtUtc);
    }
}

/// <summary>
/// EF mapping for durable GitHub comment target anchors.
/// </summary>
/// <remarks>
/// Anchors are logged and durable, unlike the lease table. They cache the
/// external GitHub comment id for one Zeeq render target so the normal writer
/// path can update directly. They do not store rendered comment content; stale
/// or missing rows are repaired by scanning GitHub for Zeeq markers.
/// </remarks>
internal sealed class GitHubCommentAnchorConfiguration
    : IEntityTypeConfiguration<GitHubCommentAnchor>
{
    public void Configure(EntityTypeBuilder<GitHubCommentAnchor> entity)
    {
        entity.ToTable("code_review_github_comment_anchors");
        entity.HasKey(anchor => anchor.TargetKey);

        entity.Property(anchor => anchor.TargetKey).HasMaxLength(512);
        entity.Property(anchor => anchor.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(anchor => anchor.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(anchor => anchor.OwnerQualifiedRepoName).IsRequired().HasMaxLength(512);
        entity
            .Property(anchor => anchor.Kind)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion<string>();
        entity.Property(anchor => anchor.ScopeKey).IsRequired().HasMaxLength(256);
        entity.Property(anchor => anchor.UpdatedAtUtc).IsRequired();

        // Enforce the logical target identity even if a caller builds an
        // incorrect TargetKey string. TargetKey stays the primary key because it
        // is the compact lookup value used by messages and leases.
        entity
            .HasIndex(anchor => new
            {
                anchor.OrganizationId,
                anchor.RepositoryId,
                anchor.PullRequestNumber,
                anchor.Kind,
                anchor.ScopeKey,
            })
            .IsUnique();
        // Operational views and repair jobs start from a repository/PR.
        entity.HasIndex(anchor => new
        {
            anchor.RepositoryId,
            anchor.PullRequestNumber,
            anchor.Kind,
        });
        // Lets diagnostics find recently repaired or created anchors.
        entity.HasIndex(anchor => new { anchor.OrganizationId, anchor.UpdatedAtUtc });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(anchor => anchor.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasForeignKey(anchor => anchor.RepositoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
