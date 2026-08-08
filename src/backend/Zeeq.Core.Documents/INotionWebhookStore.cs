using Zeeq.Core.Models;

namespace Zeeq.Core.Documents;

/// <summary>
/// Atomically manages the security-sensitive webhook state of a Notion-backed library.
/// </summary>
public interface INotionWebhookStore
{
    /// <summary>
    /// Stores the one-time Notion verification token when the callback is current and the
    /// library does not already have one.
    /// </summary>
    Task<NotionWebhookVerificationCaptureResult> TryCaptureVerificationTokenAsync(
        string organizationId,
        string libraryId,
        int callbackTokenSerial,
        EncryptedValue verificationToken,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Records or validates the workspace and subscription observed on a signed webhook event.
    /// </summary>
    Task<NotionWebhookEventObservationResult> ObserveSignedEventAsync(
        string organizationId,
        string libraryId,
        int callbackTokenSerial,
        string workspaceId,
        string subscriptionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken
    );
}

/// <summary>Outcome of attempting to persist Notion's one-time verification token.</summary>
public enum NotionWebhookVerificationCaptureResult
{
    /// <summary>The token and library reference were persisted.</summary>
    Stored = 0,

    /// <summary>The library already references a verification token.</summary>
    AlreadyStored = 1,

    /// <summary>No Notion-backed library matched the organization and library ids.</summary>
    NotFound = 2,

    /// <summary>The callback serial no longer matches the library configuration.</summary>
    StaleCallback = 3,
}

/// <summary>Outcome of observing a signed Notion webhook event.</summary>
public enum NotionWebhookEventObservationResult
{
    /// <summary>The first signed event established the subscription and activation time.</summary>
    Activated = 0,

    /// <summary>The event matches the subscription already recorded for the library.</summary>
    Current = 1,

    /// <summary>No Notion-backed library matched the organization and library ids.</summary>
    NotFound = 2,

    /// <summary>The callback serial no longer matches the library configuration.</summary>
    StaleCallback = 3,

    /// <summary>The event workspace does not match the configured workspace.</summary>
    WorkspaceMismatch = 4,

    /// <summary>The event subscription does not match the previously activated subscription.</summary>
    SubscriptionMismatch = 5,

    /// <summary>The library has not captured a verification token yet.</summary>
    VerificationIncomplete = 6,
}
