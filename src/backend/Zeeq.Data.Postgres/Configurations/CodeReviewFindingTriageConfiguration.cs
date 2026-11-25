using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>Maps the durable, tenant-scoped finding triage snapshot.</summary>
internal sealed class CodeReviewFindingTriageConfiguration
    : IEntityTypeConfiguration<CodeReviewFindingTriage>
{
    public void Configure(EntityTypeBuilder<CodeReviewFindingTriage> entity)
    {
        entity.ToTable("code_review_finding_triages", table =>
        {
            table.HasCheckConstraint(
                "ck_cr_triages_pr_positive",
                "pull_request_number IS NULL OR pull_request_number > 0"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_line_positive",
                "line_number IS NULL OR line_number > 0"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_severity",
                "severity IN ('Critical', 'Major', 'Minor', 'Suggestion', 'Comment')"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_disposition",
                "disposition IS NULL OR disposition IN ('FalsePositive', 'Deferred')"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_approval_state",
                "approval_state IN ('Open', 'Proposed', 'Confirmed')"
            );
            table.HasCheckConstraint("ck_cr_triages_channel", "channel IN ('Ui', 'GitHub', 'Mcp')");
            table.HasCheckConstraint(
                "ck_cr_triages_reason_length",
                "current_reason IS NULL OR char_length(current_reason) <= 1000"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_disposition_state",
                "(disposition IS NULL) = (approval_state = 'Open')"
            );
            table.HasCheckConstraint(
                "ck_cr_triages_confirmed_timestamp",
                "(approval_state = 'Confirmed') = (confirmed_at_utc IS NOT NULL)"
            );
        });
        entity.HasKey(x => new { x.OrganizationId, x.Id });
        entity.Property(x => x.Id).HasMaxLength(128);
        entity.Property(x => x.OrganizationId).HasMaxLength(128);
        entity.Property(x => x.TeamId).HasMaxLength(128);
        entity.Property(x => x.RepositoryId).HasMaxLength(128);
        entity.Property(x => x.OwnerQualifiedRepoName).HasMaxLength(512);
        entity.Property(x => x.ReviewGroupId).HasMaxLength(128);
        entity.Property(x => x.SourceReviewId).HasMaxLength(128);
        entity.Property(x => x.OccurrenceKey).HasMaxLength(32);
        entity.Property(x => x.SemanticId).HasMaxLength(128);
        entity.Property(x => x.Facet).HasMaxLength(128);
        entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Disposition).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.ApprovalState).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Channel).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.AgentSessionId).HasMaxLength(128);
        entity.Property(x => x.SourceUrl).HasMaxLength(2048);
        entity.Property(x => x.ProposedByUserId).HasMaxLength(128);
        entity.Property(x => x.ConfirmedByUserId).HasMaxLength(128);
        entity.Property(x => x.ClearedByUserId).HasMaxLength(128);
        entity.Property(x => x.Embedding).HasColumnType("halfvec(768)");
        entity.Property(x => x.EmbeddingModel).HasMaxLength(128);
        entity
            .HasIndex(x => new
            {
                x.OrganizationId,
                x.SourceReviewId,
                x.OccurrenceKey,
            })
            .IsUnique()
            .HasDatabaseName("ux_cr_triages_review_occurrence");
        entity
            .HasIndex(x => new
            {
                x.OrganizationId,
                x.RepositoryId,
                x.PullRequestNumber,
            })
            .HasFilter("pull_request_number IS NOT NULL")
            .HasDatabaseName("ix_cr_triages_pr_scope");
        entity
            .HasIndex(x => new { x.OrganizationId, x.ReviewGroupId })
            .HasFilter("review_group_id IS NOT NULL")
            .HasDatabaseName("ix_cr_triages_group_scope");
        entity
            .HasIndex(x => new { x.OrganizationId, x.OwnerQualifiedRepoName })
            .HasFilter("disposition = 'FalsePositive' AND approval_state = 'Confirmed'")
            .HasDatabaseName("ix_cr_triages_fp_by_repo_name");
        entity
            .HasIndex(x => new { x.OrganizationId, x.RepositoryId })
            .HasFilter(
                "repository_id IS NOT NULL AND disposition = 'FalsePositive' AND approval_state = 'Confirmed'"
            )
            .HasDatabaseName("ix_cr_triages_fp_by_repo_id");
        entity
            .HasIndex(x => new
            {
                x.UpdatedAtUtc,
                x.OrganizationId,
                x.Id,
            })
            .HasFilter("disposition = 'FalsePositive' AND approval_state = 'Confirmed'")
            .HasDatabaseName("ix_cr_triages_embed_queue");
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_cr_triages_org");
        entity
            .HasOne<CodeRepository>()
            .WithMany()
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .HasForeignKey(x => new { x.OrganizationId, x.RepositoryId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_cr_triages_repo");
    }
}
