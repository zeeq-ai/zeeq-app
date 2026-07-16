using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for GitHub webhook delivery idempotency rows.
/// </summary>
/// <remarks>
/// GitHub can replay deliveries, and Zeeq queue processing can retry after
/// partial failures. This store is the ingress checkpoint that prevents one
/// delivery ID from creating duplicate PR, review, or comment work.
///
/// Delivery claims are disposable infrastructure state. They are intentionally
/// not a webhook audit log, and schema changes may drop/recreate the backing
/// table while this feature remains pre-production rather than migrating old
/// claim rows forward.
/// </remarks>
public interface IGitHubWebhookDeliveryStore
{
    /// <summary>
    /// Attempts to claim a GitHub delivery ID for processing.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="WebhookDeliveryClaimResult.Claimed" /> only for the
    /// processing attempt that should publish follow-up work. Duplicate callers
    /// receive <see cref="WebhookDeliveryClaimResult.InProgress" /> or
    /// <see cref="WebhookDeliveryClaimResult.AlreadyProcessed" />.
    /// </remarks>
    Task<WebhookDeliveryClaimResult> ClaimAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Marks a claimed delivery as processed.
    /// </summary>
    Task MarkProcessedAsync(string deliveryId, CancellationToken cancellationToken);
}
