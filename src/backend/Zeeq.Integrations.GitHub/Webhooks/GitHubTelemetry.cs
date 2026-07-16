using System.Diagnostics.Metrics;
using Zeeq.Core.Common;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Metrics emitted by the GitHub integration.
/// </summary>
/// <remarks>
/// These counters are owned by the GitHub integration because they describe
/// provider-ingress behavior rather than generic queue or HTTP behavior. They
/// complement the ASP.NET Core request metrics and the later Brighter queue
/// metrics:
///
/// - HTTP metrics answer whether GitHub reached the route.
/// - These counters answer how Zeeq classified the delivery.
/// - Queue metrics answer whether accepted tenant work was published and
///   consumed.
///
/// Keep labels low-cardinality. Event names such as <c>pull_request</c> are
/// safe; delivery ids, repository names, and installation ids belong on traces
/// and structured logs instead of metric tags.
/// </remarks>
internal static class GitHubTelemetry
{
    /// <summary>
    /// Counts GitHub deliveries that reached the Zeeq processor.
    /// </summary>
    /// <remarks>
    /// This is incremented after Octokit has accepted the request and dispatched
    /// a typed event. It intentionally does not count signature failures because
    /// the SDK rejects those before processor code runs.
    /// </remarks>
    public static readonly Counter<long> WebhooksReceived =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq.github.webhook.received");

    /// <summary>
    /// Counts rejected GitHub webhook deliveries when Zeeq owns the rejection path.
    /// </summary>
    /// <remarks>
    /// The current Octokit ASP.NET Core adapter rejects invalid signatures before
    /// this integration code is invoked, so this counter is reserved for future
    /// cases where Zeeq adds an explicit rejection hook or wrapper. Do not
    /// remove it unless the architecture docs are updated too; the metric name is
    /// part of the planned observability contract.
    /// </remarks>
    public static readonly Counter<long> WebhooksRejected =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq.github.webhook.rejected");

    /// <summary>
    /// Counts valid GitHub deliveries that do not produce queue work.
    /// </summary>
    /// <remarks>
    /// During this ingress slice most configured events land here because the
    /// repository-mapping and queue-publishing adapters are deliberately
    /// deferred. Later, this counter should still be used for unsupported events,
    /// disabled repositories, missing mappings, and other accepted no-op paths.
    /// </remarks>
    public static readonly Counter<long> WebhooksNoOp = ZeeqTelemetry.Metrics.CreateCounter<long>(
        "zeeq.github.webhook.no_op"
    );
}
