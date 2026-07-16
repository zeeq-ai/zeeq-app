using Microsoft.Extensions.Logging;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.Installation;
using Octokit.Webhooks.Events.InstallationRepositories;
using Octokit.Webhooks.Events.InstallationTarget;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Handles GitHub App installation lifecycle webhook deliveries.
/// </summary>
/// <remarks>
/// These events keep installation and repository mappings healthy after the
/// install callback has linked a GitHub installation to a Zeeq organization.
/// Phase one acknowledges them as pass-through events because callback linking
/// remains the only path that creates durable installation rows.
/// </remarks>
public partial class ZeeqGitHubWebhookEventProcessor
{
    /// <summary>
    /// Reconciles installation lifecycle state, then logs and traces the delivery.
    /// </summary>
    /// <remarks>
    /// The browser install callback still owns the first durable link between a
    /// Zeeq organization and a GitHub installation, so this does not create
    /// unlinked installation rows. It does, however, keep an already-linked
    /// row's suspend/delete/repository-selection state current — see
    /// <see cref="ReconcileInstallationLifecycleAsync"/> for why that matters.
    /// </remarks>
    protected override async ValueTask ProcessInstallationWebhookAsync(
        WebhookHeaders headers,
        InstallationEvent installationEvent,
        InstallationAction action,
        CancellationToken cancellationToken = default
    )
    {
        await ReconcileInstallationLifecycleAsync(installationEvent, action, cancellationToken);
        await HandleInstallationPassThrough(headers, installationEvent, action);
    }

    /// <summary>
    /// Persists suspend, unsuspend, delete, and permission-acceptance state so
    /// Zeeq's local installation row does not go stale between browser
    /// install-callback runs.
    /// </summary>
    /// <remarks>
    /// GitHub stops delivering webhooks entirely for a suspended or deleted
    /// installation, but that state change only reaches Zeeq through this
    /// event. Before this reconciliation, <c>SuspendedAtUtc</c>/<c>DeletedAtUtc</c>
    /// were only ever set once, at initial callback link time, so a later
    /// suspension was invisible locally: the installation row still read as
    /// active while GitHub silently stopped sending anything for it.
    /// </remarks>
    private async ValueTask ReconcileInstallationLifecycleAsync(
        InstallationEvent installationEvent,
        InstallationAction action,
        CancellationToken cancellationToken
    )
    {
        if (!IsLifecycleReconciliationAction(action))
        {
            return;
        }

        var installation = installationEvent.Installation;

        await installationStore.ApplyLifecycleEventAsync(
            installationId: installation.Id,
            repositorySelection: installation.RepositorySelection.ToString(),
            suspendedAtUtc: action == InstallationAction.Unsuspend
                ? null
                : installation.SuspendedAt,
            deletedAtUtc: action == InstallationAction.Deleted ? DateTimeOffset.UtcNow : null,
            cancellationToken: cancellationToken
        );

        LogLifecycleReconciled(
            logger,
            installation.Id,
            FormatInstallationAction(action),
            installation.RepositorySelection.ToString()
        );
    }

    /// <summary>
    /// Identifies installation actions that change suspend/delete/permission state.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <see cref="InstallationAction.Created"/>: that action
    /// carries no suspend/delete state of its own, and the browser install
    /// callback (not this reconciliation path) owns first-link creation.
    /// </remarks>
    private static bool IsLifecycleReconciliationAction(InstallationAction action) =>
        action == InstallationAction.Suspend
        || action == InstallationAction.Unsuspend
        || action == InstallationAction.Deleted
        || action == InstallationAction.NewPermissionsAccepted;

    /// <summary>
    /// Acknowledges repository add/remove events for an existing installation.
    /// </summary>
    /// <remarks>
    /// Later repository-management work will reconcile these changes into
    /// Zeeq repository mappings. In this slice the event is intentionally a
    /// traced no-op so GitHub delivery retries do not build up.
    /// </remarks>
    protected override ValueTask ProcessInstallationRepositoriesWebhookAsync(
        WebhookHeaders headers,
        InstallationRepositoriesEvent installationRepositoriesEvent,
        InstallationRepositoriesAction action,
        CancellationToken cancellationToken = default
    ) => HandleInstallationRepositoriesPassThrough(headers, installationRepositoriesEvent, action);

    /// <summary>
    /// Acknowledges installation target rename events.
    /// </summary>
    /// <remarks>
    /// This event helps keep organization/user display names fresh after the
    /// repository-mapping slice exists. The immutable GitHub ids remain the
    /// durable lookup keys, so phase one can safely log and acknowledge it.
    /// </remarks>
    protected override ValueTask ProcessInstallationTargetWebhookAsync(
        WebhookHeaders headers,
        InstallationTargetEvent installationTargetEvent,
        InstallationTargetAction action,
        CancellationToken cancellationToken = default
    ) => HandleInstallationTargetPassThrough(headers, installationTargetEvent, action);

    /// <summary>
    /// Records an installation event as an acknowledged no-op for this phase.
    /// </summary>
    private ValueTask HandleInstallationPassThrough(
        WebhookHeaders headers,
        InstallationEvent installationEvent,
        InstallationAction action
    ) =>
        HandlePassThrough(
            headers,
            installationEvent,
            FormatInstallationAction(action),
            "installation"
        );

    /// <summary>
    /// Records an installation repository membership event as an acknowledged no-op for this phase.
    /// </summary>
    private ValueTask HandleInstallationRepositoriesPassThrough(
        WebhookHeaders headers,
        InstallationRepositoriesEvent installationRepositoriesEvent,
        InstallationRepositoriesAction action
    ) =>
        HandlePassThrough(
            headers,
            installationRepositoriesEvent,
            FormatInstallationRepositoriesAction(action),
            "installation_repositories"
        );

    /// <summary>
    /// Records an installation target rename event as an acknowledged no-op for this phase.
    /// </summary>
    private ValueTask HandleInstallationTargetPassThrough(
        WebhookHeaders headers,
        InstallationTargetEvent installationTargetEvent,
        InstallationTargetAction action
    ) =>
        HandlePassThrough(
            headers,
            installationTargetEvent,
            FormatInstallationTargetAction(action),
            "installation_target"
        );

    /// <summary>
    /// Converts Octokit installation action values to GitHub payload strings.
    /// </summary>
    private static string FormatInstallationAction(InstallationAction action) =>
        action switch
        {
            var value when value == InstallationAction.Created => InstallationActionValue.Created,
            var value when value == InstallationAction.Deleted => InstallationActionValue.Deleted,
            var value when value == InstallationAction.NewPermissionsAccepted =>
                InstallationActionValue.NewPermissionsAccepted,
            var value when value == InstallationAction.Suspend => InstallationActionValue.Suspend,
            var value when value == InstallationAction.Unsuspend =>
                InstallationActionValue.Unsuspend,
            _ => string.Empty,
        };

    /// <summary>
    /// Converts Octokit installation-repositories action values to GitHub payload strings.
    /// </summary>
    private static string FormatInstallationRepositoriesAction(
        InstallationRepositoriesAction action
    ) =>
        action switch
        {
            var value when value == InstallationRepositoriesAction.Added =>
                InstallationRepositoriesActionValue.Added,
            var value when value == InstallationRepositoriesAction.Removed =>
                InstallationRepositoriesActionValue.Removed,
            _ => string.Empty,
        };

    /// <summary>
    /// Converts Octokit installation-target action values to GitHub payload strings.
    /// </summary>
    private static string FormatInstallationTargetAction(InstallationTargetAction action) =>
        action switch
        {
            var value when value == InstallationTargetAction.Renamed =>
                InstallationTargetActionValue.Renamed,
            _ => string.Empty,
        };

    [LoggerMessage(
        EventId = 3110,
        Level = LogLevel.Information,
        Message = "🔧  GitHub installation {InstallationId} lifecycle action {Action} reconciled; repository selection {RepositorySelection}."
    )]
    private static partial void LogLifecycleReconciled(
        ILogger logger,
        long installationId,
        string action,
        string repositorySelection
    );
}
