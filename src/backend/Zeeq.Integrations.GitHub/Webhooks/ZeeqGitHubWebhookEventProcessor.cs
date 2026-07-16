using System.Diagnostics;
using System.Reflection;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;
using Octokit.Webhooks;
using Paramore.Brighter;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Processes typed GitHub App webhook deliveries after Octokit validates them.
/// </summary>
/// <remarks>
/// <see cref="GitHubWebhookEndpointMapping" /> maps the anonymous HTTP route and
/// lets Octokit validate the GitHub signature. This processor is the first
/// Zeeq-owned point in the flow. It starts the Zeeq root telemetry span,
/// logs delivery metadata, and temporarily acknowledges configured events as
/// no-ops until the queue-backed adapters are implemented.
///
/// Keep domain work out of this class. The long-term flow is:
/// GitHub HTTP delivery -> Octokit validation/deserialization -> this processor
/// -> repository/org resolution -> <c>IZeeqMessagePublisher</c> -> durable
/// queue handlers. That separation lets the public webhook route return quickly,
/// keeps GitHub retries from running code-review work inline, and gives the
/// queue layer a single place to own idempotency and retries.
///
/// The current slice intentionally stops at logging and telemetry for several
/// events. The focused partial files group each override with its local handler
/// so later slices can replace one no-op path at a time with queue-backed
/// behavior.
/// </remarks>
public partial class ZeeqGitHubWebhookEventProcessor(
    ILogger<ZeeqGitHubWebhookEventProcessor> logger,
    GitHubWebhookRepositoryGate repositoryGate,
    IGitHubInstallationStore installationStore,
    IZeeqMessagePublisher publisher,
    IPullRequestRecordStore pullRequestStore
) : WebhookEventProcessor
{
    /// <summary>
    /// Records an enabled event that currently has no queue work.
    /// </summary>
    /// <remarks>
    /// Pass-through is intentional: GitHub should see a successful delivery for
    /// configured events even before each event has product behavior. The log
    /// and metric leave a breadcrumb for operators without causing retries.
    /// </remarks>
    private ValueTask HandlePassThrough(
        WebhookHeaders headers,
        object? webhookEvent,
        string action,
        string fallbackEventName
    )
    {
        var metadata = GetMetadata(headers, webhookEvent, action, fallbackEventName);

        using var trace = StartWebhookTrace(metadata);
        RecordNoOp(metadata.EventName);
        LogPassThrough(
            logger,
            metadata.EventName,
            metadata.Action,
            metadata.DeliveryId,
            metadata.RepositoryFullName,
            metadata.InstallationId
        );

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records an event that will become actionable in a later queue-backed slice.
    /// </summary>
    /// <remarks>
    /// These events are not ignored forever. They are called out separately from
    /// pass-through events because future work will replace this no-op with
    /// repository mapping, idempotent delivery claiming, and tenant message
    /// publication.
    /// </remarks>
    private async ValueTask HandleActionableAsync<TMessage>(
        WebhookHeaders headers,
        object? webhookEvent,
        string action,
        string fallbackEventName,
        Func<
            GitHubWebhookMetadata,
            GitHubWebhookRepositoryMapping,
            ZeeqTraceContext,
            TMessage?
        > createMessage,
        CancellationToken cancellationToken
    )
        where TMessage : class, IRequest
    {
        var metadata = GetMetadata(headers, webhookEvent, action, fallbackEventName);

        using var trace = StartWebhookTrace(metadata);
        var gateResult = await repositoryGate.ResolveAsync(
            metadata.RepositoryFullName,
            metadata.DeliveryId,
            cancellationToken
        );

        if (!gateResult.IsResolved || gateResult.Repository is null)
        {
            RecordNoOp(metadata.EventName);
            LogActionableRepositoryMissing(
                logger,
                metadata.EventName,
                metadata.Action,
                metadata.DeliveryId,
                metadata.RepositoryFullName,
                metadata.InstallationId
            );

            return;
        }

        var traceContext = ZeeqTelemetry.CaptureCurrentTraceContext();
        var message = createMessage(metadata, gateResult.Repository, traceContext);
        if (message is null)
        {
            RecordNoOp(metadata.EventName);
            LogActionableFiltered(
                logger,
                metadata.EventName,
                metadata.Action,
                metadata.DeliveryId,
                metadata.RepositoryFullName,
                metadata.InstallationId
            );

            return;
        }

        await publisher.PublishAsync(message, cancellationToken);

        LogActionablePublished(
            logger,
            metadata.EventName,
            metadata.Action,
            metadata.DeliveryId,
            gateResult.Repository.Id,
            gateResult.Repository.OrganizationId
        );
    }

    /// <summary>
    /// Records an event that will become actionable in a later queue-backed slice.
    /// </summary>
    /// <remarks>
    /// Kept for any event-specific path that should still be acknowledged before
    /// its durable message contract exists. Current primary actionable events use
    /// <see cref="HandleActionableAsync{TMessage}"/>.
    /// </remarks>
    private ValueTask HandleActionableDeferred(
        WebhookHeaders headers,
        object? webhookEvent,
        string action,
        string fallbackEventName
    )
    {
        var metadata = GetMetadata(headers, webhookEvent, action, fallbackEventName);

        using var trace = StartWebhookTrace(metadata);
        RecordNoOp(metadata.EventName);

        LogActionableDeferred(
            logger,
            metadata.EventName,
            metadata.Action,
            metadata.DeliveryId,
            metadata.RepositoryFullName,
            metadata.InstallationId
        );

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds the normalized metadata used by traces, metrics, and logs.
    /// </summary>
    /// <remarks>
    /// Octokit gives us typed event payloads but no single shared interface for
    /// repository and installation metadata. This phase extracts the small
    /// common subset needed for observability. Later adapter work should prefer
    /// typed payload access when it maps event fields into durable messages.
    /// </remarks>
    private static GitHubWebhookMetadata GetMetadata(
        WebhookHeaders headers,
        object? webhookEvent,
        string action,
        string fallbackEventName
    ) =>
        new(
            EventName: string.IsNullOrWhiteSpace(headers.Event) ? fallbackEventName : headers.Event,
            Action: action,
            DeliveryId: headers.Delivery ?? string.Empty,
            RepositoryFullName: ExtractNestedValue(webhookEvent, "Repository", "FullName"),
            InstallationId: ExtractNestedValue(webhookEvent, "Installation", "Id")
        );

    /// <summary>
    /// Starts the Zeeq domain span for a GitHub delivery.
    /// </summary>
    /// <remarks>
    /// The ASP.NET Core instrumentation already creates an HTTP span. This
    /// child span gives operators a stable domain name of
    /// <c>github.webhook.{event}.{action}</c> and carries GitHub tags that later
    /// queue messages can propagate as <see cref="ZeeqTraceContext" />. Keep
    /// it parented to the request span so one webhook delivery reads as a single
    /// trace from HTTP entry, through queue publication, and into the consumers
    /// that create comments or reactions.
    /// </remarks>
    private static Activity? StartWebhookTrace(GitHubWebhookMetadata metadata)
    {
        GitHubTelemetry.WebhooksReceived.Add(
            1,
            new KeyValuePair<string, object?>("github.event", metadata.EventName)
        );

        return ZeeqTelemetry.Trace(
            [
                ("github.delivery_id", metadata.DeliveryId),
                ("github.event", metadata.EventName),
                ("github.action", metadata.Action),
                ("github.repo", metadata.RepositoryFullName),
                ("github.installation_id", metadata.InstallationId),
            ],
            traceName: CreateWebhookTraceName(metadata.EventName, metadata.Action)
        );
    }

    /// <summary>
    /// Builds the low-cardinality domain activity name for a GitHub delivery.
    /// </summary>
    /// <remarks>
    /// The action belongs in the activity name because the same GitHub event can
    /// drive very different behavior. For example, <c>pull_request.opened</c>
    /// creates review work, while <c>pull_request.closed</c> should be easy to
    /// distinguish in traces during test runs.
    /// </remarks>
    private static string CreateWebhookTraceName(string eventName, string action) =>
        string.IsNullOrWhiteSpace(action)
            ? $"github.webhook.{eventName}"
            : $"github.webhook.{eventName}.{action}";

    /// <summary>
    /// Counts webhook deliveries that produce no durable queue work in this slice.
    /// </summary>
    /// <remarks>
    /// No-op volume is important during rollout. It distinguishes healthy
    /// acknowledged deliveries from deliveries that are missing repository
    /// mappings, unsupported by the current slice, or intentionally deferred.
    /// </remarks>
    private static void RecordNoOp(string eventName)
    {
        GitHubTelemetry.WebhooksNoOp.Add(
            1,
            new KeyValuePair<string, object?>("github.event", eventName)
        );
    }

    /// <summary>
    /// Extracts a nested metadata value from the known Octokit payload shape.
    /// </summary>
    /// <remarks>
    /// NOTE: This is intentionally narrow and temporary. It keeps this ingress
    /// slice small while all handled events are still logged/no-op only. When
    /// queue adapters are implemented, replace this reflection-based helper with
    /// typed metadata extraction in the event-specific adapters so payload shape
    /// changes fail at compile time.
    /// </remarks>
    private static string ExtractNestedValue(
        object? source,
        string ownerPropertyName,
        string valuePropertyName
    )
    {
        var owner = GetDeclaredPropertyValue(source, ownerPropertyName);
        var value = GetDeclaredPropertyValue(owner, valuePropertyName);

        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Resolves a public instance property by name, preferring the most-derived
    /// declaration when a type hides a base property of the same name.
    /// </summary>
    /// <remarks>
    /// Several Octokit webhook event types (e.g. <c>InstallationEvent</c>,
    /// <c>InstallationRepositoriesEvent</c>) redeclare <c>Installation</c> with
    /// <c>new</c> to expose a richer type than the base <c>WebhookEvent</c>
    /// property. <see cref="Type.GetProperty(string)"/> throws
    /// <see cref="AmbiguousMatchException"/> in that situation because it does
    /// not understand <c>new</c>-hiding the way the C# compiler does. Walking
    /// the type chain with <see cref="BindingFlags.DeclaredOnly"/> and
    /// returning the first match reproduces normal C# member-hiding resolution.
    /// </remarks>
    private static object? GetDeclaredPropertyValue(object? source, string propertyName)
    {
        if (source is null)
        {
            return null;
        }

        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            );

            if (property is not null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    /// <summary>
    /// Normalized webhook metadata used for first-slice observability.
    /// </summary>
    /// <remarks>
    /// Keep this record intentionally small. Durable queue messages should carry
    /// richer typed identities once repository mapping and event adapters are
    /// added.
    /// </remarks>
    private sealed record GitHubWebhookMetadata(
        string EventName,
        string Action,
        string DeliveryId,
        string RepositoryFullName,
        string InstallationId
    )
    {
        public long InstallationIdAsLong =>
            long.TryParse(InstallationId, out var installationId) ? installationId : 0;
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook pass-through event {EventName}.{Action} delivery {DeliveryId} repository {RepositoryFullName} installation {InstallationId}."
    )]
    private static partial void LogPassThrough(
        ILogger logger,
        string eventName,
        string action,
        string deliveryId,
        string repositoryFullName,
        string installationId
    );

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook actionable event {EventName}.{Action} delivery {DeliveryId} repository {RepositoryFullName} installation {InstallationId} acknowledged as deferred."
    )]
    private static partial void LogActionableDeferred(
        ILogger logger,
        string eventName,
        string action,
        string deliveryId,
        string repositoryFullName,
        string installationId
    );

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook actionable event {EventName}.{Action} delivery {DeliveryId} repository {RepositoryFullName} installation {InstallationId} has no configured repository mapping."
    )]
    private static partial void LogActionableRepositoryMissing(
        ILogger logger,
        string eventName,
        string action,
        string deliveryId,
        string repositoryFullName,
        string installationId
    );

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook actionable event {EventName}.{Action} delivery {DeliveryId} repository {RepositoryFullName} installation {InstallationId} was filtered before queue publish."
    )]
    private static partial void LogActionableFiltered(
        ILogger logger,
        string eventName,
        string action,
        string deliveryId,
        string repositoryFullName,
        string installationId
    );

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook actionable event {EventName}.{Action} delivery {DeliveryId} published for repository {RepositoryId} organization {OrganizationId}."
    )]
    private static partial void LogActionablePublished(
        ILogger logger,
        string eventName,
        string action,
        string deliveryId,
        string repositoryId,
        string organizationId
    );
}
