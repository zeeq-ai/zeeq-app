using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Core.Security;
using Zeeq.Platform.Messaging;

namespace Zeeq.Integrations.Notion;

/// <summary>Validates Notion webhook ingress and hands signed events to durable messaging.</summary>
public sealed partial class NotionWebhookHandler(
    NotionCallbackTokenProtector callbackTokens,
    NotionWebhookSignatureVerifier signatures,
    ILibraryDocumentStore libraries,
    IEncryptedValueStore encryptedValues,
    EncryptedValueEncryptionService encryption,
    INotionWebhookStore webhookState,
    IZeeqMessagePublisher publisher,
    ILogger<NotionWebhookHandler> logger
) : IEndpointHandler
{
    private const string SignatureHeaderName = "X-Notion-Signature";

    /// <summary>Handles an exact raw request body for one opaque callback capability.</summary>
    public async Task<NotionWebhookIngressResult> HandleAsync(
        string? callbackToken,
        byte[] rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "notion.webhook.receive",
            ActivityKind.Internal
        );
        NotionWebhookTelemetry.Received.Add(1);

        if (!callbackTokens.TryUnprotect(callbackToken, out var callback) || callback is null)
        {
            return Reject(NotionWebhookIngressResult.NotFound, "invalid_callback");
        }

        activity?.SetTag("organization.id", callback.OrganizationId);
        activity?.SetTag("library.id", callback.LibraryId);

        var library = await libraries.GetLibraryByIdAsync(
            callback.OrganizationId,
            callback.LibraryId,
            cancellationToken
        );
        var notion = library?.ExternalSource?.Notion;
        if (
            library?.SourceKind != RepositorySourceKind.Notion.ToString()
            || notion is null
            || notion.CallbackTokenSerial != callback.CallbackTokenSerial
        )
        {
            return Reject(NotionWebhookIngressResult.NotFound, "stale_callback");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return Reject(NotionWebhookIngressResult.BadRequest, "malformed_json");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Reject(NotionWebhookIngressResult.BadRequest, "invalid_root");
            }

            if (document.RootElement.TryGetProperty("verification_token", out var challenge))
            {
                return await CaptureChallengeAsync(callback, challenge, cancellationToken);
            }

            if (notion.VerificationTokenValueId is null)
            {
                return Reject(NotionWebhookIngressResult.Unauthorized, "verification_incomplete");
            }

            var encryptedToken = await encryptedValues.FindActiveAsync(
                callback.OrganizationId,
                notion.VerificationTokenValueId,
                cancellationToken
            );
            if (encryptedToken is null)
            {
                return Reject(
                    NotionWebhookIngressResult.Unauthorized,
                    "verification_secret_missing"
                );
            }

            var verificationToken = await encryption.DecryptAsync(
                encryptedToken,
                EncryptedValueKind.SecretString,
                cancellationToken
            );
            if (
                verificationToken is null
                || !signatures.IsValid(rawBody, verificationToken, signatureHeader)
            )
            {
                return Reject(NotionWebhookIngressResult.Unauthorized, "invalid_signature");
            }

            NotionWebhookEvent? webhookEvent;
            try
            {
                webhookEvent = document.RootElement.Deserialize(
                    NotionWebhookJsonContext.Default.NotionWebhookEvent
                );
            }
            catch (JsonException)
            {
                return Reject(NotionWebhookIngressResult.BadRequest, "invalid_event_json");
            }

            if (!IsValid(webhookEvent))
            {
                return Reject(NotionWebhookIngressResult.BadRequest, "invalid_event_fields");
            }

            await publisher.PublishAsync(
                new NotionPageChangeWebhookReceived
                {
                    OrganizationId = callback.OrganizationId,
                    TeamId = library.TeamId,
                    LibraryId = callback.LibraryId,
                    CallbackTokenSerial = callback.CallbackTokenSerial,
                    SubscriptionId = webhookEvent!.SubscriptionId!,
                    WorkspaceId = webhookEvent.WorkspaceId!,
                    IntegrationId = webhookEvent.IntegrationId,
                    NotionEventId = webhookEvent.Id!,
                    EventType = webhookEvent.Type!,
                    EntityId = webhookEvent.Entity!.Id!,
                    EntityType = webhookEvent.Entity.Type!,
                    AttemptNumber = webhookEvent.AttemptNumber,
                    EventTimestamp = webhookEvent.Timestamp,
                    TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
                },
                cancellationToken
            );

            NotionWebhookTelemetry.Published.Add(1);
            LogEventPublished(
                logger,
                webhookEvent.Id!,
                webhookEvent.Type!,
                callback.LibraryId,
                callback.OrganizationId
            );

            return NotionWebhookIngressResult.Accepted;
        }
    }

    private async Task<NotionWebhookIngressResult> CaptureChallengeAsync(
        NotionCallbackPayload callback,
        JsonElement challenge,
        CancellationToken cancellationToken
    )
    {
        if (
            challenge.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(challenge.GetString())
        )
        {
            return Reject(NotionWebhookIngressResult.BadRequest, "invalid_challenge");
        }

        var now = DateTimeOffset.UtcNow;
        var encryptedToken = await encryption.EncryptAsync(
            callback.OrganizationId,
            EncryptedValueKind.SecretString,
            "Notion webhook verification token",
            challenge.GetString()!,
            now,
            cancellationToken
        );
        var result = await webhookState.TryCaptureVerificationTokenAsync(
            callback.OrganizationId,
            callback.LibraryId,
            callback.CallbackTokenSerial,
            encryptedToken,
            now,
            cancellationToken
        );

        var ingressResult = result switch
        {
            NotionWebhookVerificationCaptureResult.Stored => NotionWebhookIngressResult.Accepted,
            NotionWebhookVerificationCaptureResult.AlreadyStored =>
                NotionWebhookIngressResult.Conflict,
            NotionWebhookVerificationCaptureResult.NotFound
            or NotionWebhookVerificationCaptureResult.StaleCallback =>
                NotionWebhookIngressResult.NotFound,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };

        if (ingressResult == NotionWebhookIngressResult.Accepted)
        {
            NotionWebhookTelemetry.ChallengesCaptured.Add(1);
            LogChallengeCaptured(logger, callback.LibraryId, callback.OrganizationId);
        }
        else
        {
            Reject(ingressResult, result.ToString());
        }

        return ingressResult;
    }

    private NotionWebhookIngressResult Reject(NotionWebhookIngressResult result, string reason)
    {
        NotionWebhookTelemetry.Rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
        LogRejected(logger, reason, result.ToString());
        return result;
    }

    private static bool IsValid(NotionWebhookEvent? webhookEvent) =>
        webhookEvent is { AttemptNumber: > 0, Entity: { } entity }
        && !string.IsNullOrWhiteSpace(webhookEvent.Id)
        && !string.IsNullOrWhiteSpace(webhookEvent.Type)
        && !string.IsNullOrWhiteSpace(webhookEvent.SubscriptionId)
        && !string.IsNullOrWhiteSpace(webhookEvent.WorkspaceId)
        && !string.IsNullOrWhiteSpace(entity.Id)
        && !string.IsNullOrWhiteSpace(entity.Type);

    /// <summary>Header used by Notion for signed webhook events.</summary>
    public static string SignatureHeader => SignatureHeaderName;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Captured Notion webhook challenge for library {LibraryId} in org {OrganizationId}."
    )]
    private static partial void LogChallengeCaptured(
        ILogger logger,
        string libraryId,
        string organizationId
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Published Notion webhook event {NotionEventId} ({EventType}) for library {LibraryId} in org {OrganizationId}."
    )]
    private static partial void LogEventPublished(
        ILogger logger,
        string notionEventId,
        string eventType,
        string libraryId,
        string organizationId
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rejected Notion webhook: {Reason} ({IngressResult})."
    )]
    private static partial void LogRejected(ILogger logger, string reason, string ingressResult);
}

/// <summary>Transport-neutral outcomes returned by <see cref="NotionWebhookHandler"/>.</summary>
public enum NotionWebhookIngressResult
{
    /// <summary>The challenge was captured or the event was durably published.</summary>
    Accepted = 0,

    /// <summary>The callback capability does not resolve to current library state.</summary>
    NotFound = 1,

    /// <summary>The request JSON or required payload fields are malformed.</summary>
    BadRequest = 2,

    /// <summary>The signed branch lacks a valid current signature.</summary>
    Unauthorized = 3,

    /// <summary>A verification token was already captured.</summary>
    Conflict = 4,
}
