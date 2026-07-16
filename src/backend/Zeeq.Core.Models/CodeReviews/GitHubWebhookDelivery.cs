namespace Zeeq.Core.Models;

/// <summary>
/// Idempotency record for a GitHub webhook delivery ID.
/// </summary>
/// <remarks>
/// This model is a delivery claim, not a durable webhook audit log. Webhook
/// ingress writes this row before it publishes queue work so Zeeq can claim
/// each GitHub delivery once and skip duplicate replays.
///
/// The intended storage tradeoff is operational efficiency over recovery
/// durability: Postgres storage may keep this table unlogged to reduce WAL for
/// high-volume webhook traffic, with pg_cron deleting old claims in small
/// batches. Losing claim rows after an unclean Postgres restart is acceptable
/// because it only weakens retry dedupe; downstream PR/review gates still need
/// to tolerate a replayed delivery. This feature has not been deployed to
/// production, so migrations may drop and recreate the claim table instead of
/// preserving existing rows.
/// </remarks>
public sealed class GitHubWebhookDelivery
{
    /// <summary>
    /// GitHub delivery GUID from <c>X-GitHub-Delivery</c>.
    /// </summary>
    public required string DeliveryId { get; init; }

    /// <summary>
    /// UTC time when Zeeq claimed the delivery.
    /// </summary>
    public DateTimeOffset ClaimedAtUtc { get; init; }

    /// <summary>
    /// UTC time when the claimed delivery reached a processed state.
    /// </summary>
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
