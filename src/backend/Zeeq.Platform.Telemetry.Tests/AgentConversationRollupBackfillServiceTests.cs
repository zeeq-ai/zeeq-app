using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Processing;

namespace Zeeq.Platform.Telemetry.Tests;

public sealed class AgentConversationRollupBackfillServiceTests
{
    [Test]
    public async Task RunBackfillCycleAsync_ExcludesTimedOutConversationForRemainderOfCycle()
    {
        var timedOutKey = new AgentConversationKey("org-a", "conversation-timeout");
        var completedKey = new AgentConversationKey("org-b", "conversation-completed");
        var fakeStore = new FakeBackfillStore(
            [
                new(AgentConversationRollupBackfillStatus.TimedOut, timedOutKey),
                new(AgentConversationRollupBackfillStatus.Completed, completedKey),
                new(AgentConversationRollupBackfillStatus.NoWork),
            ]
        );
        var service = CreateService(fakeStore);

        var completed = await service.RunBackfillCycleAsync(CancellationToken.None);

        await Assert.That(completed).IsEqualTo(1);
        await Assert.That(fakeStore.Calls).Count().IsEqualTo(3);
        await Assert
            .That(fakeStore.Calls.Select(call => call.TargetVersion))
            .IsEquivalentTo(
                [
                    AgentConversationRollupVersion.Current,
                    AgentConversationRollupVersion.Current,
                    AgentConversationRollupVersion.Current,
                ]
            );
        await Assert.That(fakeStore.Calls[0].ExcludedKeys).IsEmpty();
        await Assert.That(fakeStore.Calls[1].ExcludedKeys).IsEquivalentTo([timedOutKey]);
        await Assert.That(fakeStore.Calls[2].ExcludedKeys).IsEquivalentTo([timedOutKey]);
    }

    [Test]
    public async Task RunBackfillCycleAsync_StopsAtConfiguredCycleLimit()
    {
        var fakeStore = new FakeBackfillStore(
            [
                new(
                    AgentConversationRollupBackfillStatus.Completed,
                    new AgentConversationKey("org-a", "conversation-1")
                ),
                new(
                    AgentConversationRollupBackfillStatus.Completed,
                    new AgentConversationKey("org-a", "conversation-2")
                ),
                new(
                    AgentConversationRollupBackfillStatus.Completed,
                    new AgentConversationKey("org-a", "conversation-3")
                ),
            ]
        );
        var service = CreateService(
            fakeStore,
            new TelemetrySettings
            {
                ConversationRollupBackfillMaxConversationsPerCycle = 2,
            }
        );

        var completed = await service.RunBackfillCycleAsync(CancellationToken.None);

        await Assert.That(completed).IsEqualTo(2);
        await Assert.That(fakeStore.Calls).Count().IsEqualTo(2);
    }

    private static AgentConversationRollupBackfillService CreateService(
        FakeBackfillStore store,
        TelemetrySettings? settings = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentConversationRollupBackfillStore>(store);
        var provider = services.BuildServiceProvider(validateScopes: true);

        return new AgentConversationRollupBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            settings ?? new TelemetrySettings(),
            NullLogger<AgentConversationRollupBackfillService>.Instance
        );
    }

    private sealed class FakeBackfillStore(
        IReadOnlyList<AgentConversationRollupBackfillResult> results
    ) : IAgentConversationRollupBackfillStore
    {
        private int nextResultIndex;

        public List<BackfillCall> Calls { get; } = [];

        public Task<AgentConversationRollupBackfillResult> BackfillNextAsync(
            int targetVersion,
            TimeSpan statementTimeout,
            IReadOnlySet<AgentConversationKey> excludedKeys,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(new(targetVersion, statementTimeout, excludedKeys.ToArray()));

            var result =
                nextResultIndex < results.Count
                    ? results[nextResultIndex]
                    : new AgentConversationRollupBackfillResult(
                        AgentConversationRollupBackfillStatus.NoWork
                    );
            nextResultIndex++;

            return Task.FromResult(result);
        }
    }

    private sealed record BackfillCall(
        int TargetVersion,
        TimeSpan StatementTimeout,
        IReadOnlyList<AgentConversationKey> ExcludedKeys
    );
}
