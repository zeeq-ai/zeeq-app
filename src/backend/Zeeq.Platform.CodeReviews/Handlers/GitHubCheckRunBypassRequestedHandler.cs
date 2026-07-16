using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Consumes bypass-check-request work from the GitHub feedback-command pipeline.
/// </summary>
[ConfigureConsumer<GitHubCheckRunBypassRequested>(
    "github.check_run.bypass_requested.handler",
    // ITenantMessage already fans this out across every tenant-tier bucket
    // (20 by default) — noOfPerformers multiplies that, it doesn't add to it.
    noOfPerformers: 1,
    bufferSize: 8,
    visibleTimeoutSeconds: 120
)]
public sealed class GitHubCheckRunBypassRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    ICheckRunService checkRunService
) : ZeeqMessageHandler<GitHubCheckRunBypassRequested>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<GitHubCheckRunBypassRequested> HandleMessageAsync(
        GitHubCheckRunBypassRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);

        await checkRunService.BypassAsync(
            message.OrganizationId,
            message.RepositoryId,
            message.PullRequestNumber,
            message.CommentAuthorLogin,
            cancellationToken
        );

        return message;
    }

    private static System.Diagnostics.Activity? StartActivity(GitHubCheckRunBypassRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            "github.check_run.bypass_requested",
            System.Diagnostics.ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("organization.id", message.OrganizationId),
                new("github.repo", message.OwnerQualifiedRepoName),
                new("pull_request.number", message.PullRequestNumber),
                new("github.delivery_id", message.GitHubDeliveryId),
            ]
        );
    }
}
