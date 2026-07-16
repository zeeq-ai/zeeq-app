using System.Diagnostics.Metrics;
using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Metrics for GitHub reaction writes initiated by code-review feedback flows.
/// </summary>
/// <remarks>
/// The reaction queue handler lives in the provider-neutral code-review package
/// because it is part of the feedback workflow. The metric name remains GitHub
/// specific because the side effect is a GitHub reaction API call. Keep labels
/// low-cardinality: outcomes and target kinds are safe, while delivery ids and
/// repository names belong on traces/logs.
/// </remarks>
internal static class GitHubReactionTelemetry
{
    /// <summary>
    /// Counts attempts to add acknowledgement reactions to GitHub comments.
    /// </summary>
    public static readonly Counter<long> ReactionWrites =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq.github.reaction.write");
}
