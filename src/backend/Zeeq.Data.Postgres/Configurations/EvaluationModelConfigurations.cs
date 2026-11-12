using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for reviewer backtesting datasets.
/// </summary>
/// <remarks>
/// Five evaluation tables are ordinary logged, unpartitioned tables retained indefinitely
/// in v1. Every primary key is organization-qualified so cross-organization reads cannot
/// succeed by id alone, and every new table uses composite organization-qualified foreign
/// keys where the parent exposes an alternate key.
/// </remarks>
internal sealed class CodeReviewEvaluationDatasetConfiguration
    : IEntityTypeConfiguration<CodeReviewEvaluationDataset>
{
    public void Configure(EntityTypeBuilder<CodeReviewEvaluationDataset> entity)
    {
        entity.ToTable("code_review_evaluation_datasets");
        entity.HasKey(dataset => new { dataset.OrganizationId, dataset.Id });
        // Repository-qualified alternate key. Child cases and runs reference this shape so a
        // dataset id from another repository in the same organization cannot be attached.
        entity.HasAlternateKey(dataset => new
        {
            dataset.OrganizationId,
            dataset.RepositoryId,
            dataset.Id,
        });

        entity.Property(dataset => dataset.Id).HasMaxLength(128);
        entity.Property(dataset => dataset.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(dataset => dataset.TeamId).HasMaxLength(128);
        entity.Property(dataset => dataset.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(dataset => dataset.Name).IsRequired().HasMaxLength(256);
        entity.Property(dataset => dataset.Description).HasMaxLength(2000);
        entity.Property(dataset => dataset.Version).IsRequired();
        entity.Property(dataset => dataset.CreateRequestKey).IsRequired().HasMaxLength(128);
        entity.Property(dataset => dataset.ArchivedAtUtc);
        entity.Property(dataset => dataset.CreatedAtUtc).IsRequired();
        entity.Property(dataset => dataset.UpdatedAtUtc).IsRequired();
        entity.Property(dataset => dataset.DisabledAtUtc);

        // Create retries return the same dataset instead of creating a second one.
        entity
            .HasIndex(dataset => new { dataset.OrganizationId, dataset.CreateRequestKey })
            .IsUnique();
        // Curation lists live datasets for one repository; the composite keeps the row id
        // stable as the tie-breaker.
        entity.HasIndex(dataset => new
        {
            dataset.OrganizationId,
            dataset.RepositoryId,
            dataset.ArchivedAtUtc,
            dataset.Id,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(dataset => dataset.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(dataset => new { dataset.OrganizationId, dataset.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
        // NOTE: composite organization-qualified FK. Some deployed baselines are missing the
        // repository (organization_id, id) unique key even though the model declares it, so the
        // evaluation migration adds that key with a catalog guard before creating these FKs.
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasPrincipalKey(repository => new { repository.OrganizationId, repository.Id })
            .HasForeignKey(dataset => new { dataset.OrganizationId, dataset.RepositoryId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for captured pull request cases inside an evaluation dataset.
/// </summary>
internal sealed class CodeReviewEvaluationCaseConfiguration
    : IEntityTypeConfiguration<CodeReviewEvaluationCase>
{
    /// <summary>
    /// Compares reference-set collections by canonical content so EF tracks wholesale replacement.
    /// </summary>
    private static readonly ValueComparer<CodeReviewEvaluationReferenceSet[]> ReferenceSetsComparer =
        new(
            (left, right) => Serialize(left) == Serialize(right),
            value => Serialize(value).GetHashCode(),
            value => CodeReviewEvaluationReferenceSetSerializer.Deserialize(Serialize(value))
        );

    private static string Serialize(CodeReviewEvaluationReferenceSet[]? value) =>
        CodeReviewEvaluationReferenceSetSerializer.Serialize(
            value ?? Array.Empty<CodeReviewEvaluationReferenceSet>()
        );

    public void Configure(EntityTypeBuilder<CodeReviewEvaluationCase> entity)
    {
        entity.ToTable("code_review_evaluation_cases");
        entity.HasKey(item => new { item.OrganizationId, item.Id });

        entity.Property(item => item.Id).HasMaxLength(128);
        entity.Property(item => item.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(item => item.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(item => item.DatasetId).IsRequired().HasMaxLength(128);
        entity.Property(item => item.PullRequestNumber).IsRequired();
        entity.Property(item => item.PullRequestTitle).IsRequired().HasMaxLength(1024);
        entity.Property(item => item.PullRequestRecordId).HasMaxLength(128);
        entity.Property(item => item.SnapshotStorageUri).IsRequired().HasMaxLength(2048);
        entity.Property(item => item.SnapshotSha256).IsRequired().HasMaxLength(64);
        entity.Property(item => item.SnapshotByteCount).IsRequired();
        entity.Property(item => item.SnapshotSchemaVersion).IsRequired();
        entity.Property(item => item.BaseSha).IsRequired().HasMaxLength(128);
        entity.Property(item => item.HeadSha).IsRequired().HasMaxLength(128);
        entity
            .Property(item => item.PullRequestState)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(item => item.CapturedAtUtc).IsRequired();
        entity.Property(item => item.ChangedFileCount).IsRequired();
        entity.Property(item => item.OmittedFileCount).IsRequired();
        entity.Property(item => item.DeclaredChangedFileCount).IsRequired();
        entity.Property(item => item.RemovedAtUtc);
        entity.Property(item => item.CreatedAtUtc).IsRequired();
        entity.Property(item => item.UpdatedAtUtc).IsRequired();
        entity.Property(item => item.DisabledAtUtc);

        entity
            .Property(item => item.ReferenceSets)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                value => CodeReviewEvaluationReferenceSetSerializer.Serialize(value),
                json => CodeReviewEvaluationReferenceSetSerializer.Deserialize(json),
                ReferenceSetsComparer
            );

        // One membership row per pull request. Soft-removed rows keep participating so a
        // re-add reuses the same case identity instead of inserting a duplicate.
        entity
            .HasIndex(item => new
            {
                item.OrganizationId,
                item.DatasetId,
                item.PullRequestNumber,
            })
            .IsUnique();
        // Curation list and reference scope reads walk live membership in id order.
        entity.HasIndex(item => new
        {
            item.OrganizationId,
            item.DatasetId,
            item.RemovedAtUtc,
            item.Id,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(item => item.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewEvaluationDataset>()
            .WithMany()
            .HasPrincipalKey(dataset => new
            {
                dataset.OrganizationId,
                dataset.RepositoryId,
                dataset.Id,
            })
            .HasForeignKey(item => new
            {
                item.OrganizationId,
                item.RepositoryId,
                item.DatasetId,
            })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasPrincipalKey(repository => new { repository.OrganizationId, repository.Id })
            .HasForeignKey(item => new { item.OrganizationId, item.RepositoryId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for immutable reviewer agent revisions.
/// </summary>
internal sealed class CodeReviewerAgentRevisionConfigurationMapping
    : IEntityTypeConfiguration<CodeReviewerAgentRevision>
{
    public void Configure(EntityTypeBuilder<CodeReviewerAgentRevision> entity)
    {
        entity.ToTable("code_reviewer_agent_revisions");
        entity.HasKey(revision => new { revision.OrganizationId, revision.Id });
        // Repository-qualified alternate key so a run in one repository cannot reference a
        // revision that belongs to another repository in the same organization.
        entity.HasAlternateKey(revision => new
        {
            revision.OrganizationId,
            revision.RepositoryId,
            revision.Id,
        });

        entity.Property(revision => revision.Id).HasMaxLength(128);
        entity.Property(revision => revision.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(revision => revision.TeamId).HasMaxLength(128);
        entity.Property(revision => revision.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(revision => revision.AgentId).IsRequired().HasMaxLength(128);
        entity.Property(revision => revision.ContentHash).IsRequired().HasMaxLength(64);
        entity.Property(revision => revision.Label).IsRequired().HasMaxLength(200);
        entity.Property(revision => revision.CreatedByUserId).IsRequired().HasMaxLength(128);
        entity.Property(revision => revision.CreatedAtUtc).IsRequired();
        entity.Property(revision => revision.DisabledAtUtc);

        // NOTE: stored as jsonb text with a manual canonical serializer rather than an owned
        // JSON navigation. Revisions are immutable, always written whole, and never queried by
        // an inner configuration value, so a jsonb document keeps the read path simple and
        // avoids materialization failures if the payload ever drifts.
        entity
            .Property(revision => revision.Configuration)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                configuration => CodeReviewerAgentRevisionContent.Serialize(configuration),
                json => CodeReviewerAgentRevisionContent.Deserialize(json)
            );

        // Content addressing: identical configuration content never creates a second row.
        entity
            .HasIndex(revision => new
            {
                revision.OrganizationId,
                revision.RepositoryId,
                revision.AgentId,
                revision.ContentHash,
            })
            .IsUnique();
        // Revision history is read newest first per agent.
        entity.HasIndex(revision => new
        {
            revision.OrganizationId,
            revision.AgentId,
            revision.CreatedAtUtc,
            revision.Id,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(revision => revision.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasPrincipalKey(repository => new { repository.OrganizationId, repository.Id })
            .HasForeignKey(revision => new { revision.OrganizationId, revision.RepositoryId })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewerAgent>()
            .WithMany()
            .HasPrincipalKey(agent => new
            {
                agent.OrganizationId,
                agent.RepositoryId,
                agent.Id,
            })
            .HasForeignKey(revision => new
            {
                revision.OrganizationId,
                revision.RepositoryId,
                revision.AgentId,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for evaluation run history and ownership.
/// </summary>
/// <remarks>
/// The partial unique index on organization for queued/running rows is the durable
/// "one active evaluation per organization" invariant. Admission also takes the organization
/// advisory lock, so the index is a backstop rather than the contention point.
/// </remarks>
internal sealed class CodeReviewEvaluationRunConfiguration
    : IEntityTypeConfiguration<CodeReviewEvaluationRun>
{
    public void Configure(EntityTypeBuilder<CodeReviewEvaluationRun> entity)
    {
        entity.ToTable("code_review_evaluation_runs");
        entity.HasKey(run => new { run.OrganizationId, run.Id });

        entity.Property(run => run.Id).HasMaxLength(128);
        entity.Property(run => run.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(run => run.TeamId).HasMaxLength(128);
        entity.Property(run => run.RepositoryId).IsRequired().HasMaxLength(128);
        entity.Property(run => run.DatasetId).IsRequired().HasMaxLength(128);
        entity.Property(run => run.DatasetVersion).IsRequired();
        entity.Property(run => run.AgentId).IsRequired().HasMaxLength(128);
        entity.Property(run => run.SavedRevisionId).HasMaxLength(128);
        entity.Property(run => run.ManifestJson).HasColumnType("jsonb").IsRequired();
        entity.Property(run => run.RequestedByUserId).IsRequired().HasMaxLength(128);
        entity.Property(run => run.State).IsRequired().HasMaxLength(32).HasConversion<string>();
        entity.Property(run => run.ParentRunId).HasMaxLength(128);
        entity.Property(run => run.RequestKey).IsRequired().HasMaxLength(128);
        entity.Property(run => run.LeaseToken).HasMaxLength(128);
        entity.Property(run => run.LeaseExpiresAtUtc);
        entity.Property(run => run.CancelRequestedAtUtc);
        entity.Property(run => run.StartedAtUtc);
        entity.Property(run => run.CompletedAtUtc);
        entity.Property(run => run.ErrorCode).HasMaxLength(128);
        entity.Property(run => run.FailureMessage).HasMaxLength(4096);
        entity.Property(run => run.CreatedAtUtc).IsRequired();
        entity.Property(run => run.UpdatedAtUtc).IsRequired();
        entity.Property(run => run.DisabledAtUtc);

        // Request-key idempotency is scoped to the organization.
        entity.HasIndex(run => new { run.OrganizationId, run.RequestKey }).IsUnique();
        // At most one queued or running evaluation per organization. NOTE: state is stored
        // as the enum name, so the filter must use the persisted string values.
        entity
            .HasIndex(run => run.OrganizationId)
            .IsUnique()
            .HasFilter("state IN ('Queued', 'Running')")
            .HasDatabaseName("ux_code_review_evaluation_runs_active_per_org");
        // History reads are newest first within one dataset.
        entity.HasIndex(run => new
        {
            run.OrganizationId,
            run.RepositoryId,
            run.DatasetId,
            run.CreatedAtUtc,
            run.Id,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(run => run.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasPrincipalKey(repository => new { repository.OrganizationId, repository.Id })
            .HasForeignKey(run => new { run.OrganizationId, run.RepositoryId })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewEvaluationDataset>()
            .WithMany()
            .HasPrincipalKey(dataset => new
            {
                dataset.OrganizationId,
                dataset.RepositoryId,
                dataset.Id,
            })
            .HasForeignKey(run => new
            {
                run.OrganizationId,
                run.RepositoryId,
                run.DatasetId,
            })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewerAgentRevision>()
            .WithMany()
            .HasPrincipalKey(revision => new
            {
                revision.OrganizationId,
                revision.RepositoryId,
                revision.Id,
            })
            .HasForeignKey(run => new
            {
                run.OrganizationId,
                run.RepositoryId,
                run.SavedRevisionId,
            })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewEvaluationRun>()
            .WithMany()
            .HasPrincipalKey(parent => new { parent.OrganizationId, parent.Id })
            .HasForeignKey(run => new { run.OrganizationId, run.ParentRunId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for per-case evaluation results.
/// </summary>
/// <remarks>
/// Results key off the manifest case id rather than the mutable dataset case, so dataset
/// edits and removals never rewrite run history. Successful terminal grades are immutable;
/// retries live in child runs.
/// </remarks>
internal sealed class CodeReviewEvaluationCaseResultConfiguration
    : IEntityTypeConfiguration<CodeReviewEvaluationCaseResult>
{
    public void Configure(EntityTypeBuilder<CodeReviewEvaluationCaseResult> entity)
    {
        entity.ToTable("code_review_evaluation_case_results");
        entity.HasKey(result => new { result.OrganizationId, result.Id });

        entity.Property(result => result.Id).HasMaxLength(128);
        entity.Property(result => result.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(result => result.RunId).IsRequired().HasMaxLength(128);
        entity.Property(result => result.ManifestCaseId).IsRequired().HasMaxLength(128);
        entity.Property(result => result.PullRequestNumber).IsRequired();
        entity
            .Property(result => result.Phase)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity
            .Property(result => result.Outcome)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(result => result.CandidateStorageUri).HasMaxLength(2048);
        entity.Property(result => result.CandidateSha256).HasMaxLength(64);
        entity.Property(result => result.CandidateByteCount);
        entity.Property(result => result.CandidateFindingCount).IsRequired();
        entity.Property(result => result.MatchedReferenceCount).IsRequired();
        entity.Property(result => result.MissedReferenceCount).IsRequired();
        entity.Property(result => result.ExpectedReferenceCount).IsRequired();
        entity.Property(result => result.AdditionalFindingCount).IsRequired();
        entity.Property(result => result.CandidateFindingsJson).HasColumnType("jsonb");
        entity.Property(result => result.JudgeMatchesJson).HasColumnType("jsonb");
        entity.Property(result => result.JudgeReasonsJson).HasColumnType("jsonb");
        entity.Property(result => result.ReviewerProvenanceJson).HasColumnType("jsonb");
        entity.Property(result => result.JudgeProvenanceJson).HasColumnType("jsonb");
        // NOTE: token counts are nullable on purpose. Zero is a real count; null means the
        // provider did not report usage, and the two must not be conflated in provenance.
        entity.Property(result => result.ReviewerInputTokens);
        entity.Property(result => result.ReviewerOutputTokens);
        entity.Property(result => result.JudgeInputTokens);
        entity.Property(result => result.JudgeOutputTokens);
        entity.Property(result => result.EstimatedCostUsd).HasPrecision(18, 8);
        entity.Property(result => result.ErrorCode).HasMaxLength(128);
        entity.Property(result => result.FailureMessage).HasMaxLength(4096);
        entity.Property(result => result.StartedAtUtc);
        entity.Property(result => result.CompletedAtUtc);
        entity.Property(result => result.CreatedAtUtc).IsRequired();
        entity.Property(result => result.UpdatedAtUtc).IsRequired();
        entity.Property(result => result.DisabledAtUtc);

        entity
            .HasIndex(result => new
            {
                result.OrganizationId,
                result.RunId,
                result.ManifestCaseId,
            })
            .IsUnique();
        entity.HasIndex(result => new
        {
            result.OrganizationId,
            result.RunId,
            result.Phase,
            result.ManifestCaseId,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(result => result.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<CodeReviewEvaluationRun>()
            .WithMany()
            .HasPrincipalKey(run => new { run.OrganizationId, run.Id })
            .HasForeignKey(result => new { result.OrganizationId, result.RunId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
