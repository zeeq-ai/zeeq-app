using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Links a persisted pull request to every matching telemetry conversation.
/// </summary>
public sealed class TelemetryPullRequestLinkingService(IAgentTelemetryDomainStore domainStore)
    : IAgentTelemetryPullRequestLinker
{
    /// <inheritdoc />
    public async Task<int> LinkAsync(
        PullRequestRecord pullRequest,
        CancellationToken cancellationToken
    )
    {
        var conversations = await domainStore.FindForRepositoryBranchAsync(
            pullRequest.OrganizationId,
            pullRequest.OwnerQualifiedRepoName,
            pullRequest.Branch,
            cancellationToken
        );
        var created = 0;

        foreach (var conversation in conversations)
        {
            var link = new AgentPullRequestSessionLink
            {
                Id = $"aprl_{Guid.CreateVersion7():N}",
                OrganizationId = pullRequest.OrganizationId,
                PullRequestRecordId = pullRequest.Id,
                ConversationId = conversation.Id,
                LinkOrigin = AgentSessionLinkOrigin.WebhookCurated,
                LinkedAtUtc = DateTimeOffset.UtcNow,
                LinkedByUserId = null,
                IsPending = false,
            };

            if (await domainStore.TryCreatePullRequestSessionLinkAsync(link, cancellationToken))
            {
                created++;

                AgentTelemetryMetrics.RecordPullRequestLink(
                    link.OrganizationId,
                    conversation.Harness,
                    link.LinkOrigin
                );
            }
        }

        return created;
    }
}
