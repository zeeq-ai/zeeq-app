using System.Text;
using Zeeq.Core.Common;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Workflow fan-in node that aggregates validated reviewer blocks.
/// </summary>
/// <remarks>
/// Reviewer validators emit schema-valid <c>&lt;review&gt;</c> blocks. This
/// executor buffers every block delivered in the fan-in superstep and yields
/// exactly one combined body after message delivery finishes. The outer
/// <c>&lt;reviews&gt;</c> document is added by <see cref="CodeReviewAgentExecutor" />
/// so the aggregation node stays aligned with the V1 responsibility boundary.
/// </remarks>
[YieldsOutput(typeof(string))]
internal sealed partial class CodeReviewConcurrentAggregationExecutor(ILoggerFactory loggerFactory)
    : Executor<ChatMessage>("CodeReviewConcurrentAggregationExecutor")
{
    private readonly List<ChatMessage> _messages = [];
    private readonly ILogger<CodeReviewConcurrentAggregationExecutor> _logger =
        loggerFactory.CreateLogger<CodeReviewConcurrentAggregationExecutor>();

    /// <summary>
    /// Creates an aggregation executor without node-local logging.
    /// </summary>
    public CodeReviewConcurrentAggregationExecutor()
        : this(NullLoggerFactory.Instance) { }

    /// <inheritdoc />
    public override ValueTask HandleAsync(
        ChatMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        _messages.Add(message);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnMessageDeliveryFinishedAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        var output = new StringBuilder();
        var orderedMessages = _messages
            .OrderBy(message => message.AuthorName, StringComparer.Ordinal)
            .ToArray();
        var facets = string.Join(",", orderedMessages.Select(message => message.AuthorName));

        ZeeqTelemetry.AddEvent(
            [
                ("code_review.reviewer_block_count", orderedMessages.Length),
                ("code_review.reviewer_facets", facets),
            ],
            "code_review.workflow_aggregated"
        );
        LogReviewerBlocksAggregated(_logger, orderedMessages.Length, facets);

        foreach (var message in orderedMessages)
        {
            output.Append(message.Text);
        }

        _messages.Clear();

        return context.YieldOutputAsync(output.ToString(), cancellationToken);
    }

    [LoggerMessage(
        EventId = 3240,
        Level = LogLevel.Information,
        Message = "Aggregated code-review workflow blocks. ReviewerBlockCount={ReviewerBlockCount}, ReviewerFacets={ReviewerFacets}"
    )]
    private static partial void LogReviewerBlocksAggregated(
        ILogger logger,
        int reviewerBlockCount,
        string reviewerFacets
    );
}
