using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Wraps a document-library <see cref="AIFunction"/> so that every invocation runs
/// inside its own dependency-injection scope.
/// </summary>
/// <remarks>
/// Reviewer agents are built with <c>AllowConcurrentInvocation = true</c> (see
/// <see cref="Zeeq.Core.Llm.LlmClientFactory"/>), so a single model turn can fan a
/// reviewer's tool calls out concurrently. The wrapped document tools resolve a
/// <c>PostgresDbContext</c>-backed <see cref="Zeeq.Core.Documents.ILibraryDocumentStore"/>
/// from <see cref="AIFunctionArguments.Services"/>. When several concurrent calls share the
/// reviewer's single scope, they drive one <c>DbContext</c> from multiple threads and throw
/// "A second operation was started on this context instance". EF Core <c>DbContext</c>
/// instances are not thread-safe, so each concurrent tool call needs its own scoped context.
///
/// This wrapper opens a fresh child scope per invocation and points
/// <see cref="AIFunctionArguments.Services"/> at it, so
/// <see cref="CodeReviewAgentExecutor.BindServiceParameter"/> resolves a per-call store and
/// context. The scope — and the context it owns — is disposed when the call returns, which is
/// safe because the store materializes its results (mostly <c>AsNoTracking</c>) before the
/// call completes. The per-reviewer scope in
/// <see cref="CodeReviewAgentExecutor.ExecuteAsync"/> still isolates reviewers from each other;
/// this wrapper additionally isolates concurrent calls within one reviewer.
/// </remarks>
/// <param name="innerFunction">The underlying library tool function to invoke.</param>
/// <param name="rootServices">
/// The reviewer's service provider used to create a fresh child scope for each invocation.
/// </param>
/// <param name="configureScope">
/// Optional hook run against each fresh child scope before the tool executes — the code-review
/// path uses it to mark <see cref="Zeeq.Core.Documents.DocumentSearchScope"/> so document
/// stores hide review-excluded documents for that invocation only.
/// </param>
internal sealed class ScopedServiceAIFunction(
    AIFunction innerFunction,
    IServiceProvider rootServices,
    Action<IServiceProvider>? configureScope = null
) : DelegatingAIFunction(innerFunction)
{
    /// <summary>
    /// The scope hook this wrapper applies, exposed so tests can lock the production wiring
    /// (every library tool from <c>BuildLibraryTools</c> must carry
    /// <c>CodeReviewAgentExecutor.MarkCodeReviewExecutionScope</c>).
    /// </summary>
    internal Action<IServiceProvider>? ScopeConfigurator => configureScope;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        // Each concurrent tool call gets its own scope so its DbContext is never shared across
        // threads. The scope (and DbContext) is disposed once the underlying call returns.
        await using var scope = rootServices.CreateAsyncScope();

        // NOTE: correctness of the review-exclusion filter depends on this hook running before
        // the tool executes and on the tool resolving its stores from THIS scope
        // (arguments.Services). Any future path that resolves document stores outside the
        // wrapped child scope bypasses the marker and the filter (flagged by code review,
        // 2026-07-15) — keep tool service resolution on arguments.Services.
        configureScope?.Invoke(scope.ServiceProvider);
        arguments.Services = scope.ServiceProvider;

        return await InnerFunction.InvokeAsync(arguments, cancellationToken);
    }
}

/// <summary>
/// Fluent helpers for wrapping an <see cref="AIFunction"/> with per-invocation service scoping.
/// </summary>
internal static class AIFunctionScopingExtensions
{
    extension(AIFunction function)
    {
        /// <summary>
        /// Wraps this function so each invocation resolves its services from a fresh DI scope.
        /// </summary>
        /// <remarks>
        /// Keeps the scope-per-invocation intent at the call site and discoverable via IntelliSense.
        /// See <see cref="ScopedServiceAIFunction"/> for why concurrent reviewer tool calls each
        /// need their own scope (and therefore their own <c>DbContext</c>).
        /// </remarks>
        /// <param name="serviceProvider">
        /// The provider used to create a fresh child scope for each invocation.
        /// </param>
        /// <param name="configureScope">
        /// Optional hook run against each fresh child scope before the tool executes (e.g. to
        /// mark <see cref="Zeeq.Core.Documents.DocumentSearchScope"/> for the review path).
        /// </param>
        internal AIFunction WithScopedServices(
            IServiceProvider serviceProvider,
            Action<IServiceProvider>? configureScope = null
        ) => new ScopedServiceAIFunction(function, serviceProvider, configureScope);
    }
}
