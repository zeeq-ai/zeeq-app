using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.CheckRun;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Handles check-run webhook deliveries for the native bypass action button.
/// </summary>
/// <remarks>
/// When a check run has conclusion <c>action_required</c> with a Bypass action,
/// GitHub renders a button that, when clicked, sends a <c>check_run</c>
/// <c>requested_action</c> webhook. This handler resolves the PR from the
/// check run's associated pull requests and calls <see cref="ICheckRunService.BypassAsync"/>
/// directly with the sender's GitHub login.
/// </remarks>
public partial class ZeeqGitHubWebhookEventProcessor
{
    /// <summary>
    /// Processes check-run webhook events including the native bypass action button.
    /// </summary>
    protected override ValueTask ProcessCheckRunWebhookAsync(
        WebhookHeaders headers,
        CheckRunEvent checkRunEvent,
        CheckRunAction action,
        CancellationToken cancellationToken = default
    ) => HandleCheckRunAsync(headers, checkRunEvent, action, cancellationToken);

    /// <summary>
    /// Handles the check-run requested_action bypass path. When the webhook payload
    /// has no associated pull requests (for example after a stacked-branch collapse),
    /// we reverse-lookup the PR by the check run's head SHA before publishing.
    /// </summary>
    private async ValueTask HandleCheckRunAsync(
        WebhookHeaders headers,
        CheckRunEvent checkRunEvent,
        CheckRunAction action,
        CancellationToken cancellationToken
    )
    {
        if (
            action != CheckRunAction.RequestedAction
            || checkRunEvent.RequestedAction?.Identifier != "zeeq_bypass"
        )
        {
            await HandlePassThrough(
                headers,
                checkRunEvent,
                FormatCheckRunAction(action),
                "check_run"
            );
            return;
        }

        var resolvedPrNumber = await ResolveCheckRunPrNumberAsync(
            checkRunEvent,
            headers,
            action,
            cancellationToken
        );

        await HandleActionableAsync(
            headers,
            checkRunEvent,
            FormatCheckRunAction(action),
            "check_run",
            (metadata, repository, traceContext) =>
                CreateCheckRunBypassMessage(
                    metadata,
                    repository,
                    traceContext,
                    checkRunEvent,
                    resolvedPrNumber
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Resolves the pull request number from the webhook payload, falling back
    /// to a head-SHA reverse-lookup when the PR association is missing (for
    /// example after a stacked-branch collapse).
    /// </summary>
    private async ValueTask<int?> ResolveCheckRunPrNumberAsync(
        CheckRunEvent checkRunEvent,
        WebhookHeaders headers,
        CheckRunAction action,
        CancellationToken cancellationToken
    )
    {
        if (checkRunEvent.CheckRun?.PullRequests is { Count: > 0 })
        {
            return (int)checkRunEvent.CheckRun.PullRequests[0].Number;
        }

        if (checkRunEvent.CheckRun?.HeadSha is not { } headSha)
        {
            return null;
        }

        var metadata = GetMetadata(
            headers,
            checkRunEvent,
            FormatCheckRunAction(action),
            "check_run"
        );
        var gateResult = await repositoryGate.ResolveAsync(
            metadata.RepositoryFullName,
            metadata.DeliveryId,
            cancellationToken
        );
        if (!gateResult.IsResolved || gateResult.Repository is null)
        {
            return null;
        }

        var resolved = await pullRequestStore.FindByHeadShaWithCheckRunAsync(
            gateResult.Repository.OrganizationId,
            gateResult.Repository.Id,
            headSha,
            cancellationToken
        );

        return resolved?.PullRequestNumber;
    }

    /// <summary>
    /// Creates a bypass-check queue message from a check-run requested_action webhook.
    /// </summary>
    private static GitHubCheckRunBypassRequested? CreateCheckRunBypassMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        CheckRunEvent checkRunEvent,
        int? preResolvedPrNumber
    )
    {
        var prNumber = preResolvedPrNumber;
        if (prNumber is null && checkRunEvent.CheckRun?.PullRequests is { Count: > 0 })
        {
            prNumber = (int)checkRunEvent.CheckRun.PullRequests[0].Number;
        }

        if (prNumber is null)
        {
            return null;
        }

        return new()
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = prNumber.Value,
            CommentAuthorLogin = checkRunEvent.Sender?.Login ?? "unknown",
            GitHubDeliveryId = metadata.DeliveryId,
            TraceContext = traceContext,
        };
    }

    /// <summary>
    /// Converts Octokit check-run action values to GitHub payload strings.
    /// </summary>
    private static string FormatCheckRunAction(CheckRunAction action) =>
        action switch
        {
            var value when value == CheckRunAction.Created => CheckRunActionValue.Created,
            var value when value == CheckRunAction.Completed => CheckRunActionValue.Completed,
            var value when value == CheckRunAction.RequestedAction =>
                CheckRunActionValue.RequestedAction,
            var value when value == CheckRunAction.Rerequested => CheckRunActionValue.Rerequested,
            _ => string.Empty,
        };
}
