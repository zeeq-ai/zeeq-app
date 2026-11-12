using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Owns one durable evaluation run and fences all case work with its lease.</summary>
[ConfigureConsumer<CodeReviewEvaluationRunRequested>(
    "code-review.evaluation.run",
    noOfPerformers: 1,
    bufferSize: 16,
    visibleTimeoutSeconds: 900,
    pollIntervalMilliseconds: 100
)]
public sealed class CodeReviewEvaluationRunRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    IServiceScopeFactory scopes,
    ILogger<CodeReviewEvaluationRunRequestedHandler> logger,
    TimeProvider? timeProvider = null
) : ZeeqMessageHandler<CodeReviewEvaluationRunRequested>(deadLetterWriter)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task<CodeReviewEvaluationRunRequested> HandleMessageAsync(
        CodeReviewEvaluationRunRequested message,
        CancellationToken cancellationToken
    ) => await ExecuteAsync(message, cancellationToken);

    internal async Task<CodeReviewEvaluationRunRequested> ExecuteAsync(
        CodeReviewEvaluationRunRequested message,
        CancellationToken cancellationToken
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<ICodeReviewEvaluationRunStore>();
        var token = Guid.NewGuid().ToString("N");
        var claim = await runs.TryClaimAsync(
            message.OrganizationId,
            message.RunId,
            token,
            LeaseDuration,
            cancellationToken
        );
        if (
            claim.Outcome != CodeReviewEvaluationRunClaimOutcome.Claimed
            || claim.Run is null
            || claim.Manifest is null
        )
            return message;

        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = new CancellationTokenSource();
        var state = new LeaseState();
        var heartbeat = HeartbeatAsync(message, token, work, heartbeatStop.Token, state);
        var failures = 0;
        try
        {
            await Parallel.ForEachAsync(
                claim.Manifest.Cases,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = CodeReviewEvaluationLimits.MaxConcurrentCases,
                    CancellationToken = work.Token,
                },
                async (item, ct) =>
                {
                    await using var caseScope = scopes.CreateAsyncScope();
                    var executor =
                        caseScope.ServiceProvider.GetRequiredService<ICodeReviewEvaluationCaseExecutor>();
                    try
                    {
                        await executor.ExecuteAsync(claim.Run, claim.Manifest, item, token, ct);
                    }
                    catch (CodeReviewEvaluationFencedException)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failures);
                        logger.LogWarning(
                            "Evaluation case persistence failed. RunId={RunId}, CaseId={CaseId}, ErrorType={ErrorType}",
                            message.RunId,
                            item.CaseId,
                            ex.GetType().Name
                        );
                    }
                }
            );
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (CodeReviewEvaluationFencedException ex)
        {
            if (ex.ReasonCode == "cancel_requested")
                Interlocked.Exchange(ref state.CancelRequested, 1);
            else
                Interlocked.Exchange(ref state.Lost, 1);
            work.Cancel();
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }

        if (Volatile.Read(ref state.Lost) != 0 || cancellationToken.IsCancellationRequested)
            return message;
        var caseResults =
            scope.ServiceProvider.GetRequiredService<ICodeReviewEvaluationCaseResultStore>();
        var rows = await caseResults.ListForRunAsync(
            message.OrganizationId,
            message.RunId,
            cancellationToken
        );
        var finalState =
            Volatile.Read(ref state.CancelRequested) != 0 ? CodeReviewEvaluationRunState.Cancelled
            : failures == 0
            && rows.Count == claim.Manifest.Cases.Count
            && rows.All(x => x.Outcome == CodeReviewEvaluationCaseOutcome.Graded)
                ? CodeReviewEvaluationRunState.Completed
            : CodeReviewEvaluationRunState.Incomplete;
        try
        {
            await runs.FinalizeAsync(
                new(message.OrganizationId, message.RunId, token, finalState, null, null),
                cancellationToken
            );
        }
        catch (CodeReviewEvaluationFencedException)
        {
            logger.LogWarning("Evaluation finalization was fenced. RunId={RunId}", message.RunId);
        }
        return message;
    }

    private async Task HeartbeatAsync(
        CodeReviewEvaluationRunRequested message,
        string token,
        CancellationTokenSource work,
        CancellationToken stop,
        LeaseState state
    )
    {
        try
        {
            using var timer = new PeriodicTimer(
                HeartbeatInterval,
                timeProvider ?? TimeProvider.System
            );
            while (await timer.WaitForNextTickAsync(stop))
            {
                await using var scope = scopes.CreateAsyncScope();
                var store =
                    scope.ServiceProvider.GetRequiredService<ICodeReviewEvaluationRunStore>();
                var renewal = await store.RenewLeaseAsync(
                    message.OrganizationId,
                    message.RunId,
                    token,
                    LeaseDuration,
                    stop
                );
                if (!renewal.Renewed)
                {
                    Interlocked.Exchange(ref state.Lost, 1);
                    work.Cancel();
                    return;
                }
                if (renewal.CancelRequested)
                {
                    Interlocked.Exchange(ref state.CancelRequested, 1);
                    work.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Evaluation lease renewal failed. RunId={RunId}", message.RunId);
            Interlocked.Exchange(ref state.Lost, 1);
            work.Cancel();
        }
    }

    private sealed class LeaseState
    {
        public int Lost;
        public int CancelRequested;
    }
}
