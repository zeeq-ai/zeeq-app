using System.Diagnostics.Metrics;
using Zeeq.Core.Common;

namespace Zeeq.Integrations.Notion;

/// <summary>Low-cardinality metrics for Notion webhook ingress.</summary>
internal static class NotionWebhookTelemetry
{
    public static readonly Counter<long> Received = ZeeqTelemetry.Metrics.CreateCounter<long>(
        "zeeq.notion.webhook.received"
    );

    public static readonly Counter<long> Rejected = ZeeqTelemetry.Metrics.CreateCounter<long>(
        "zeeq.notion.webhook.rejected"
    );

    public static readonly Counter<long> Published = ZeeqTelemetry.Metrics.CreateCounter<long>(
        "zeeq.notion.webhook.published"
    );

    public static readonly Counter<long> ChallengesCaptured =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq.notion.webhook.challenge_captured");
}
