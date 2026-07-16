namespace Zeeq.Core.Models;

/// <summary>
/// Result of claiming a webhook delivery id for idempotent processing.
/// </summary>
public enum WebhookDeliveryClaimResult
{
    /// <summary>
    /// Delivery was claimed by this processing attempt.
    /// </summary>
    Claimed = 0,

    /// <summary>
    /// Delivery already reached a terminal processed state.
    /// </summary>
    AlreadyProcessed = 1,

    /// <summary>
    /// Delivery was already claimed and has not reached a terminal state yet.
    /// </summary>
    InProgress = 2,
}
