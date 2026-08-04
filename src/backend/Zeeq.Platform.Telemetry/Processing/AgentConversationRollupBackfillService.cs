using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Finite worker that advances stale agent conversation rollups to the current version.
/// </summary>
/// <remarks>
/// Distribution is coordinated by the store's database claim query, which uses
/// <c>FOR UPDATE SKIP LOCKED</c>. This service can therefore run on every Cloud Run worker
/// instance without an application-level singleton lock.
/// </remarks>
public sealed partial class AgentConversationRollupBackfillService(
    IServiceScopeFactory scopeFactory,
    TelemetrySettings settings,
    ILogger<AgentConversationRollupBackfillService> log
) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.ConversationRollupBackfillEnabled)
        {
            LogDisabled(log, AgentConversationRollupVersion.Current);

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await RunBackfillCycleAsync(stoppingToken);

                if (completed == 0)
                {
                    await DelayUntilNextCycleAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogUnhandledError(log, exception);
                await DelayUntilNextCycleAsync(stoppingToken);
            }
        }
    }

    /// <summary>
    /// Runs one bounded backfill cycle. Public for focused tests; normal execution uses
    /// <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task<int> RunBackfillCycleAsync(CancellationToken cancellationToken)
    {
        var maxConversations = Math.Max(
            1,
            settings.ConversationRollupBackfillMaxConversationsPerCycle
        );
        var statementTimeout = TimeSpan.FromSeconds(
            Math.Max(1, settings.ConversationRollupBackfillStatementTimeoutSeconds)
        );
        var excludedKeys = new HashSet<AgentConversationKey>();
        var completed = 0;

        for (var attempt = 0; attempt < maxConversations; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<
                IAgentConversationRollupBackfillStore
            >();

            var result = await store.BackfillNextAsync(
                AgentConversationRollupVersion.Current,
                statementTimeout,
                excludedKeys,
                cancellationToken
            );

            switch (result.Status)
            {
                case AgentConversationRollupBackfillStatus.Completed:
                    completed++;
                    if (result.ConversationKey is { } completedKey)
                    {
                        AgentTelemetryMetrics.RecordConversationRollupBackfill(
                            ConversationRollupBackfillOutcome.Completed
                        );
                    }

                    break;

                case AgentConversationRollupBackfillStatus.NoWork:
                    if (completed > 0 || excludedKeys.Count > 0)
                    {
                        LogCycleComplete(log, completed, excludedKeys.Count);
                    }
                    else
                    {
                        LogCycleIdle(log);
                    }

                    return completed;

                case AgentConversationRollupBackfillStatus.TimedOut:
                    if (result.ConversationKey is { } timedOutKey)
                    {
                        excludedKeys.Add(timedOutKey);
                        AgentTelemetryMetrics.RecordConversationRollupBackfill(
                            ConversationRollupBackfillOutcome.TimedOut
                        );
                        LogTimedOut(log, timedOutKey.OrganizationId, timedOutKey.ConversationId);
                    }
                    else
                    {
                        LogClaimTimedOut(log);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(result.Status),
                        result.Status,
                        null
                    );
            }
        }

        LogCycleComplete(log, completed, excludedKeys.Count);

        return completed;
    }

    private Task DelayUntilNextCycleAsync(CancellationToken cancellationToken) =>
        Task.Delay(
            TimeSpan.FromMilliseconds(Math.Max(1, settings.ConversationRollupBackfillIdleDelayMs)),
            cancellationToken
        );

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Agent conversation rollup backfill disabled; current rollup version is {TargetVersion}"
    )]
    private static partial void LogDisabled(ILogger log, int targetVersion);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Agent conversation rollup backfill cycle completed {CompletedCount} conversations and excluded {TimedOutCount} timed-out conversations"
    )]
    private static partial void LogCycleComplete(
        ILogger log,
        int completedCount,
        int timedOutCount
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Agent conversation rollup backfill cycle found no work"
    )]
    private static partial void LogCycleIdle(ILogger log);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Agent conversation rollup backfill timed out for {OrganizationId}/{ConversationId}; excluding it for the remainder of this cycle"
    )]
    private static partial void LogTimedOut(
        ILogger log,
        string organizationId,
        string conversationId
    );

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Agent conversation rollup backfill claim timed out before a conversation was identified"
    )]
    private static partial void LogClaimTimedOut(ILogger log);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Error,
        Message = "Unhandled exception in agent conversation rollup backfill loop"
    )]
    private static partial void LogUnhandledError(ILogger log, Exception exception);
}
