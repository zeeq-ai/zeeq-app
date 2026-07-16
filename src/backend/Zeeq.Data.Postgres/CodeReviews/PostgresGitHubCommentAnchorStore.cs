using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for durable GitHub comment anchors.
/// </summary>
/// <remarks>
/// The anchor table is the fast path from a render target to a GitHub comment
/// id. It is logged and durable because it is useful across process restarts,
/// but it is still repairable. Missing or stale rows fall back to marker scans
/// against GitHub, and successful scans call <see cref="UpsertResolvedAsync" />
/// to make future writes direct again.
/// </remarks>
internal sealed class PostgresGitHubCommentAnchorStore(PostgresDbContext db)
    : IGitHubCommentAnchorStore
{
    /// <summary>
    /// Finds the stored GitHub comment id for one render target.
    /// </summary>
    public Task<GitHubCommentAnchor?> FindAsync(
        GitHubCommentTargetSelector target,
        CancellationToken cancellationToken
    ) =>
        db
            .GitHubCommentAnchors.TagWithOperationCallSite("github_comment_anchor.find")
            .FirstOrDefaultAsync(
                anchor => anchor.TargetKey == target.ToStorageKey(),
                cancellationToken
            );

    /// <summary>
    /// Creates or refreshes the direct GitHub comment pointer.
    /// </summary>
    public async Task<GitHubCommentAnchor> UpsertResolvedAsync(
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long gitHubCommentId,
        CancellationToken cancellationToken
    )
    {
        var targetKey = target.ToStorageKey();
        var now = DateTimeOffset.UtcNow;
        var existing = await db
            .GitHubCommentAnchors.TagWithOperationCallSite(
                "github_comment_anchor.upsert_find_existing"
            )
            .SingleOrDefaultAsync(anchor => anchor.TargetKey == targetKey, cancellationToken);

        if (existing is null)
        {
            existing = new GitHubCommentAnchor
            {
                TargetKey = targetKey,
                OrganizationId = target.OrganizationId,
                RepositoryId = target.RepositoryId,
                OwnerQualifiedRepoName = ownerQualifiedRepoName,
                PullRequestNumber = target.PullRequestNumber,
                Kind = target.Kind,
                ScopeKey = target.ScopeKey,
                GitHubCommentId = gitHubCommentId,
                LastResolvedAtUtc = now,
                UpdatedAtUtc = now,
            };

            db.GitHubCommentAnchors.Add(existing);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return existing;
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // NOTE: Two workers can repair or create the same anchor after
                // a marker scan. The unique key is the durable boundary; detach
                // this losing insert, read the winner, and continue through the
                // update path so the operation remains idempotent.
                db.Entry(existing).State = EntityState.Detached;
                existing = await db
                    .GitHubCommentAnchors.TagWithOperationCallSite(
                        "github_comment_anchor.upsert_find_winner"
                    )
                    .SingleAsync(anchor => anchor.TargetKey == targetKey, cancellationToken);
            }
        }

        // The target identity is immutable, but the GitHub id can change if
        // a stale anchor points to a deleted comment and the scan path finds
        // or creates the replacement.
        existing.OwnerQualifiedRepoName = ownerQualifiedRepoName;
        existing.GitHubCommentId = gitHubCommentId;
        existing.LastResolvedAtUtc = now;
        existing.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);

        return existing;
    }

    /// <summary>
    /// Detects the unique-key fallback when two anchor writers race.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
