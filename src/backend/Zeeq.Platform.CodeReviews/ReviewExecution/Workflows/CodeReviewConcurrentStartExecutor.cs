using Zeeq.Core.Common;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Workflow start node that broadcasts one review prompt to every reviewer.
/// </summary>
/// <remarks>
/// This mirrors the V1 start executor: all reviewer nodes first receive the
/// same user <see cref="ChatMessage" /> and then receive a <see cref="TurnToken" />.
/// Reviewer nodes queue the prompt and begin work in the same workflow turn, so
/// a slow reviewer does not prevent other reviewers from starting.
/// </remarks>
[SendsMessage(typeof(ChatMessage))]
[SendsMessage(typeof(TurnToken))]
internal sealed partial class CodeReviewConcurrentStartExecutor()
    : Executor("CodeReviewConcurrentStartExecutor")
{
    /// <summary>
    /// Broadcasts the shared pull request prompt body and turn token to every reviewer node.
    /// </summary>
    /// <remarks>
    /// <paramref name="sharedPullRequestPromptBody" /> is the reviewer-neutral body built by
    /// <see cref="CodeReviewUserPrompt.From" /> — it contains PR metadata, file patches, library
    /// names, and feedback guidelines, but no per-reviewer identity or previous-review content.
    /// Each <see cref="CodeReviewReviewerValidatingExecutor" /> receives it as <c>message.Text</c>
    /// and prepends its own <c>&lt;identity&gt;</c> and appends <c>&lt;previous_reviews&gt;</c>
    /// before calling the model, keeping the system prompt byte-stable for LLM prompt caching.
    /// </remarks>
    [MessageHandler]
    public async ValueTask HandleAsync(
        string sharedPullRequestPromptBody,
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        ZeeqTelemetry.AddEvent(
            [("code_review.prompt_char_count", sharedPullRequestPromptBody.Length)],
            "code_review.workflow_prompt_broadcast"
        );

        await context.SendMessageAsync(
            new ChatMessage(ChatRole.User, sharedPullRequestPromptBody),
            cancellationToken: cancellationToken
        );

        await context.SendMessageAsync(
            new TurnToken(emitEvents: false),
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .SendsMessage<ChatMessage>()
            .SendsMessage<TurnToken>()
            .ConfigureRoutes(routes => routes.AddHandler<string>(HandleAsync));
}
