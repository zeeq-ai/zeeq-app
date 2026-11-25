using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>Postgres implementation of occurrence-keyed triage and embedding leases.</summary>
internal sealed class PostgresCodeReviewFindingTriageStore(
    IDbContextFactory<PostgresDbContext> factory
) : ICodeReviewFindingTriageStore
{
    public async Task<FindingTriageWriteResult> UpsertLwwAsync(
        FindingTriageWrite write,
        CancellationToken cancellationToken = default
    )
    {
        if (
            write.Channel == CodeReviewFindingTriageChannel.GitHub
            && write.SourceEventAtUtc is null
        )
            return new(
                FindingTriageWriteStatus.Invalid,
                null,
                "GitHub source timestamp is required."
            );
        var reason = write.Reason?.Trim();
        if (
            (
                write.Operation == FindingTriageOperation.Set
                && (write.Disposition is null || string.IsNullOrWhiteSpace(reason))
            )
            || reason?.Length > 1000
        )
            return new(
                FindingTriageWriteStatus.Invalid,
                null,
                "A nonblank reason of at most 1000 characters is required."
            );
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken
            );
            var s = write.Snapshot;
            var row = await db
                .CodeReviewFindingTriages.FromSqlInterpolated(
                    $"SELECT * FROM zeeq.code_review_finding_triages WHERE organization_id = {s.OrganizationId} AND source_review_id = {s.SourceReviewId} AND occurrence_key = {s.OccurrenceKey} FOR UPDATE"
                )
                .SingleOrDefaultAsync(cancellationToken);
            if (
                write.Channel == CodeReviewFindingTriageChannel.Mcp
                && row?.ApprovalState == CodeReviewFindingApprovalState.Confirmed
            )
                return new(
                    FindingTriageWriteStatus.Forbidden,
                    row,
                    "Confirmed decisions require a human."
                );
            if (
                write.Channel == CodeReviewFindingTriageChannel.GitHub
                && row is not null
                && write.SourceEventAtUtc < row.LastSourceEventAtUtc
            )
                return new(FindingTriageWriteStatus.IgnoredStale, row);
            if (
                write.Operation == FindingTriageOperation.Confirm
                && (
                    write.Channel == CodeReviewFindingTriageChannel.Mcp
                    || row is null
                    || row.ApprovalState == CodeReviewFindingApprovalState.Open
                )
            )
                return new(FindingTriageWriteStatus.Invalid, row, "Nothing to confirm.");
            if (
                write.Operation == FindingTriageOperation.Confirm
                && write.ExpectedDisposition is not null
                && row!.Disposition != write.ExpectedDisposition
            )
                return new(FindingTriageWriteStatus.Invalid, row, "Disposition changed.");
            var disposition = write.Operation switch
            {
                FindingTriageOperation.Clear => null,
                FindingTriageOperation.Confirm => row!.Disposition,
                FindingTriageOperation.Set => write.Disposition,
                _ => throw new ArgumentOutOfRangeException(nameof(write.Operation)),
            };
            var state =
                write.Operation == FindingTriageOperation.Clear
                    ? CodeReviewFindingApprovalState.Open
                : write.Channel == CodeReviewFindingTriageChannel.Mcp
                    ? CodeReviewFindingApprovalState.Proposed
                : CodeReviewFindingApprovalState.Confirmed;
            var newReason =
                write.Operation == FindingTriageOperation.Clear ? null
                : write.Operation == FindingTriageOperation.Confirm
                && string.IsNullOrWhiteSpace(reason)
                    ? row!.CurrentReason
                : reason;
            if (
                row is not null
                && row.Disposition == disposition
                && row.ApprovalState == state
                && row.CurrentReason == newReason
            )
                return new(FindingTriageWriteStatus.Applied, row);
            var now = await db
                .Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (row is null)
            {
                row = s;
                db.CodeReviewFindingTriages.Add(row);
                db.Entry(row).Property(x => x.CreatedAtUtc).CurrentValue = now;
                row.OwnerQualifiedRepoName = row.OwnerQualifiedRepoName.ToLowerInvariant();
                if (row.PullRequestNumber <= 0)
                    db.Entry(row).Property(x => x.PullRequestNumber).CurrentValue = null;
                if (row.LineNumber <= 0)
                    db.Entry(row).Property(x => x.LineNumber).CurrentValue = null;
                if (string.IsNullOrEmpty(row.FilePath))
                    db.Entry(row).Property(x => x.FilePath).CurrentValue = null;
            }
            row.Disposition = disposition;
            row.ApprovalState = state;
            row.CurrentReason = newReason;
            row.Channel = write.Channel;
            row.AgentSessionId = write.AgentSessionId;
            row.SourceUrl = write.SourceUrl;
            row.UpdatedAtUtc = now;
            row.LastSourceEventAtUtc =
                write.Channel == CodeReviewFindingTriageChannel.GitHub
                    ? write.SourceEventAtUtc!.Value
                    : now;
            if (state == CodeReviewFindingApprovalState.Open)
            {
                row.ProposedByUserId = null;
                row.ProposedAtUtc = null;
                row.ConfirmedByUserId = null;
                row.ConfirmedAtUtc = null;
                row.ClearedByUserId = write.ActorUserId;
                row.ClearedAtUtc = now;
            }
            else if (state == CodeReviewFindingApprovalState.Proposed)
            {
                row.ProposedByUserId = write.ActorUserId;
                row.ProposedAtUtc = now;
                row.ConfirmedByUserId = null;
                row.ConfirmedAtUtc = null;
                row.ClearedByUserId = null;
                row.ClearedAtUtc = null;
            }
            else
            {
                row.ConfirmedByUserId = write.ActorUserId;
                row.ConfirmedAtUtc = now;
                row.ClearedByUserId = null;
                row.ClearedAtUtc = null;
            }
            row.Embedding = null;
            row.EmbeddingModel = null;
            row.EmbeddingStartedAt = null;
            if (db.Entry(row).State != EntityState.Added)
            {
                db.Entry(row).Property(x => x.Embedding).IsModified = true;
                db.Entry(row).Property(x => x.EmbeddingModel).IsModified = true;
                db.Entry(row).Property(x => x.EmbeddingStartedAt).IsModified = true;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new(FindingTriageWriteStatus.Applied, row);
            }
            catch (DbUpdateException ex)
                when (attempt == 0
                    && ex.InnerException
                        is PostgresException
                        {
                            SqlState: PostgresErrorCodes.UniqueViolation,
                            ConstraintName: "ux_cr_triages_review_occurrence"
                        }
                )
            {
                await tx.RollbackAsync(cancellationToken);
            }
        }
    }

    public async Task<CodeReviewFindingTriage?> FindAsync(
        string organizationId,
        string triageId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db
            .CodeReviewFindingTriages.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.Id == triageId,
                cancellationToken
            );
    }

    public async Task<IReadOnlyList<CodeReviewFindingTriage>> ListForReviewAsync(
        string organizationId,
        string reviewId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db
            .CodeReviewFindingTriages.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.SourceReviewId == reviewId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CodeReviewFindingTriage>> ListForScopeAsync(
        string organizationId,
        string? repositoryId,
        string repoNameLower,
        int? prNumber,
        string? reviewGroupId,
        string reviewId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db
            .CodeReviewFindingTriages.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Disposition != null);
        if (prNumber is > 0)
            query = query.Where(x =>
                x.RepositoryId == repositoryId
                && x.PullRequestNumber == prNumber
                && repositoryId != null
            );
        else if (reviewGroupId is not null)
            query = query.Where(x =>
                x.ReviewGroupId == reviewGroupId
                && (
                    repositoryId != null
                        ? x.RepositoryId == repositoryId
                            || (x.RepositoryId == null && x.OwnerQualifiedRepoName == repoNameLower)
                        : x.RepositoryId == null && x.OwnerQualifiedRepoName == repoNameLower
                )
            );
        else
            query = query.Where(x => x.SourceReviewId == reviewId);
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FindingTriageEmbeddingClaim>> ClaimPendingEmbeddingsAsync(
        string targetModel,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default
    )
    {
        if (batchSize <= 0 || leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db
            .CodeReviewFindingTriages.FromSqlInterpolated(
                $"""
                WITH pending AS (
                    SELECT organization_id, id FROM zeeq.code_review_finding_triages
                    WHERE disposition = 'FalsePositive' AND approval_state = 'Confirmed'
                      AND (embedding IS NULL OR embedding_model IS DISTINCT FROM {targetModel})
                      AND (embedding_started_at IS NULL OR embedding_started_at < clock_timestamp() - {leaseDuration})
                    ORDER BY updated_at_utc, organization_id, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED
                )
                UPDATE zeeq.code_review_finding_triages AS triage SET embedding_started_at = clock_timestamp()
                FROM pending WHERE triage.organization_id = pending.organization_id AND triage.id = pending.id
                RETURNING triage.*
                """
            )
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.Select(x => new FindingTriageEmbeddingClaim(
                x,
                x.UpdatedAtUtc,
                x.EmbeddingStartedAt!.Value
            ))
            .ToList();
    }

    public async Task<bool> TryCompleteEmbeddingAsync(
        string organizationId,
        string id,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset startedAtUtc,
        HalfVector vector,
        string model,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await EligibleClaim(db, organizationId, id, updatedAtUtc, startedAtUtc)
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(x => x.Embedding, vector)
                            .SetProperty(x => x.EmbeddingModel, model)
                            .SetProperty(x => x.EmbeddingStartedAt, (DateTimeOffset?)null),
                    cancellationToken
                ) == 1;
    }

    public async Task<bool> ReleaseEmbeddingClaimAsync(
        string organizationId,
        string id,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await EligibleClaim(db, organizationId, id, updatedAtUtc, startedAtUtc)
                .ExecuteUpdateAsync(
                    setters =>
                        setters.SetProperty(x => x.EmbeddingStartedAt, (DateTimeOffset?)null),
                    cancellationToken
                ) == 1;
    }

    private static IQueryable<CodeReviewFindingTriage> EligibleClaim(
        PostgresDbContext db,
        string organizationId,
        string id,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset startedAtUtc
    ) =>
        db.CodeReviewFindingTriages.Where(x =>
            x.OrganizationId == organizationId
            && x.Id == id
            && x.UpdatedAtUtc == updatedAtUtc
            && x.EmbeddingStartedAt == startedAtUtc
            && x.Disposition == CodeReviewFindingDisposition.FalsePositive
            && x.ApprovalState == CodeReviewFindingApprovalState.Confirmed
        );

    public async Task<IReadOnlyList<FindingTriageMatch>> SearchConfirmedFalsePositivesAsync(
        string organizationId,
        string? repositoryId,
        string repoNameLower,
        HalfVector vector,
        string model,
        double threshold,
        int limit,
        int? activePrNumber = null,
        string? activeReviewGroupId = null,
        string? activeReviewId = null,
        DateTimeOffset? asOfUtc = null,
        int? excludedPrNumber = null,
        CancellationToken cancellationToken = default
    )
    {
        if (threshold is < -1 or > 1 || !double.IsFinite(threshold) || limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var values = vector.ToArray();
        if (
            values.Length != 768
            || values.Any(x => !Half.IsFinite(x))
            || values.All(x => x == (Half)0)
        )
            throw new ArgumentException(
                "Vector must have 768 finite dimensions and nonzero magnitude.",
                nameof(vector)
            );
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db
            .CodeReviewFindingTriages.AsNoTracking()
            .Where(x =>
                x.OrganizationId == organizationId
                && x.Disposition == CodeReviewFindingDisposition.FalsePositive
                && x.ApprovalState == CodeReviewFindingApprovalState.Confirmed
                && x.Embedding != null
                && x.EmbeddingModel == model
                && (
                    repositoryId != null
                        ? x.RepositoryId == repositoryId
                            || (x.RepositoryId == null && x.OwnerQualifiedRepoName == repoNameLower)
                        : x.RepositoryId == null && x.OwnerQualifiedRepoName == repoNameLower
                )
            );
        if (activePrNumber is not null)
            query = query.Where(x =>
                x.PullRequestNumber == null || x.PullRequestNumber != activePrNumber
            );
        else if (activeReviewGroupId is not null)
            query = query.Where(x =>
                x.ReviewGroupId == null || x.ReviewGroupId != activeReviewGroupId
            );
        else if (activeReviewId is not null)
            query = query.Where(x => x.SourceReviewId != activeReviewId);
        if (excludedPrNumber is not null)
            query = query.Where(x =>
                x.PullRequestNumber == null || x.PullRequestNumber != excludedPrNumber
            );
        if (asOfUtc is not null)
            query = query.Where(x => x.ConfirmedAtUtc < asOfUtc);
        var matches = await query
            .Select(x => new { Row = x, Distance = x.Embedding!.CosineDistance(vector) })
            .Where(x => x.Distance <= 1 - threshold)
            .OrderBy(x => x.Distance)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return matches.Select(x => new FindingTriageMatch(x.Row, x.Distance)).ToList();
    }
}
