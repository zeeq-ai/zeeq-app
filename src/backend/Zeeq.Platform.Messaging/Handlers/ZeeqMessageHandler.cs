using Paramore.Brighter;
using Paramore.Brighter.Policies.Attributes;
using Paramore.Brighter.Policies.Handlers;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Base class for Zeeq asynchronous message handlers.
/// </summary>
/// <remarks>
/// Brighter discovers pipeline attributes from the concrete <see cref="HandleAsync"/>
/// method. This base class seals that method so retry and dead-letter fallback
/// behavior is attached consistently across feature handlers. Feature handlers
/// implement <see cref="HandleMessageAsync"/> only.
/// </remarks>
public abstract class ZeeqMessageHandler<TMessage>(IDeadLetterWriter deadLetterWriter)
    : RequestHandlerAsync<TMessage>
    where TMessage : class, IRequest
{
    /// <summary>
    /// Default Brighter resilience pipeline name used by Zeeq message handlers.
    /// </summary>
    public const string DefaultRetryPipelineName = "zeeq-message-retry";

    /// <summary>
    /// Handles the Brighter pipeline entry point for every Zeeq message handler.
    /// </summary>
    /// <remarks>
    /// Brighter invokes <see cref="RequestHandlerAsync{TRequest}.HandleAsync"/>
    /// when its message pump dispatches a queued message. Policy attributes are
    /// discovered on that pipeline method, so the retry and fallback attributes
    /// live here instead of on feature-specific handler methods. This override
    /// is sealed to keep every feature handler inside the same retry and
    /// dead-letter behavior.
    ///
    /// The feature-owned work runs in <see cref="HandleMessageAsync"/>. After it
    /// completes, this method calls the base Brighter handler so the rest of the
    /// Brighter chain can continue normally. If the retry pipeline is exhausted,
    /// the fallback policy calls <see cref="FallbackAsync"/>, which writes the
    /// failed command to the configured dead-letter sink.
    /// </remarks>
    /// <param name="command">Message command dispatched by Brighter.</param>
    /// <param name="cancellationToken">Cancellation token from the Brighter message pump.</param>
    /// <returns>The handled message for the remaining Brighter pipeline.</returns>
    /// <seealso href="https://github.com/BrighterCommand/Docs/blob/master/contents/AsyncDispatchARequest.md#async-callback-context">
    /// See more: Brighter async callback context.
    /// </seealso>
    [FallbackPolicyAsync(backstop: true, circuitBreaker: false, step: 1)]
    [UseResiliencePipelineAsync(DefaultRetryPipelineName, step: 2)]
    public sealed override async Task<TMessage> HandleAsync(
        TMessage command,
        CancellationToken cancellationToken = default
    )
    {
        // Brighter owns this setting on RequestHandlerAsync<T>. Using it here keeps
        // Zeeq feature work and the remaining Brighter chain on the same
        // continuation policy as the message pump: normally thread-pool
        // continuations, unless a host explicitly needs captured context such
        // as thread-local request state.
        var handled = await HandleMessageAsync(command, cancellationToken)
            .ConfigureAwait(ContinueOnCapturedContext);

        return await base.HandleAsync(handled, cancellationToken)
            .ConfigureAwait(ContinueOnCapturedContext);
    }

    /// <summary>
    /// Handles the feature-specific message body.
    /// </summary>
    /// <param name="message">Message being processed.</param>
    /// <param name="cancellationToken">Cancellation token from the Brighter message pump.</param>
    /// <returns>The handled message for the remaining Brighter pipeline.</returns>
    protected abstract Task<TMessage> HandleMessageAsync(
        TMessage message,
        CancellationToken cancellationToken
    );

    /// <inheritdoc />
    public override async Task<TMessage> FallbackAsync(
        TMessage command,
        CancellationToken cancellationToken = default
    )
    {
        await deadLetterWriter
            .WriteAsync(command, Context, GetFallbackException(), cancellationToken)
            .ConfigureAwait(ContinueOnCapturedContext);

        return command;
    }

    private Exception? GetFallbackException()
    {
        if (
            Context?.Bag.TryGetValue(
                FallbackPolicyHandlerRequestHandlerAsync<TMessage>.CAUSE_OF_FALLBACK_EXCEPTION,
                out var exception
            ) == true
            && exception is Exception typedException
        )
        {
            return typedException;
        }

        return null;
    }
}
