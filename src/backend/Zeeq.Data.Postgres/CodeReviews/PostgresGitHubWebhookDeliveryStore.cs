using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for GitHub webhook delivery idempotency.
/// </summary>
/// <remarks>
/// GitHub can retry the same delivery, and Zeeq may retry processing after a
/// partial failure. This table is the ingress checkpoint: a handler claims the
/// delivery before publishing queue work, then marks it processed after the
/// synchronous ingress stage reaches a terminal outcome.
/// </remarks>
internal sealed class PostgresGitHubWebhookDeliveryStore(PostgresDbContext db)
    : IGitHubWebhookDeliveryStore
{
    /// <summary>
    /// Attempts to claim one GitHub delivery ID for processing.
    /// </summary>
    /// <remarks>
    /// Insert is the fast path. A unique-key conflict means another attempt has
    /// already seen this delivery, so the existing row decides whether this is
    /// an in-progress replay or an already-processed duplicate.
    /// </remarks>
    public async Task<WebhookDeliveryClaimResult> ClaimAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        db.GitHubWebhookDeliveries.Add(delivery);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return WebhookDeliveryClaimResult.Claimed;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The attempted insert remains tracked after a failed SaveChanges.
            // Detach it before loading the durable row that won the race.
            db.Entry(delivery).State = EntityState.Detached;
        }

        var existing = await db
            .GitHubWebhookDeliveries.TagWithOperationCallSite(
                "github_webhook_delivery.claim_find_existing"
            )
            .SingleAsync(row => row.DeliveryId == delivery.DeliveryId, cancellationToken);

        return existing.ProcessedAtUtc is null
            ? WebhookDeliveryClaimResult.InProgress
            : WebhookDeliveryClaimResult.AlreadyProcessed;
    }

    /// <summary>
    /// Marks a claimed delivery as processed.
    /// </summary>
    /// <remarks>
    /// Uses a set-based update so callers do not need to load the row just to
    /// finish the ingress checkpoint.
    /// </remarks>
    public async Task MarkProcessedAsync(string deliveryId, CancellationToken cancellationToken)
    {
        await db
            .GitHubWebhookDeliveries.TagWithOperationCallSite(
                "github_webhook_delivery.mark_processed"
            )
            .Where(delivery => delivery.DeliveryId == deliveryId)
            .ExecuteUpdateAsync(
                setters =>
                    setters.SetProperty(
                        delivery => delivery.ProcessedAtUtc,
                        TimeProvider.System.GetUtcNow()
                    ),
                cancellationToken
            );
    }

    /// <summary>
    /// Detects duplicate GitHub delivery claims.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
