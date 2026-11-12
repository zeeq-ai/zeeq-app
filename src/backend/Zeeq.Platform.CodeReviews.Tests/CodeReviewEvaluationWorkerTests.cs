using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationWorkerTests
{
    [Test]
    public async Task DuplicateDeliveryDoesNotLaunchCasesOrFinalize()
    {
        var store = Substitute.For<ICodeReviewEvaluationRunStore>();
        store
            .TryClaimAsync(
                "org",
                "run",
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationRunClaimResult(
                    CodeReviewEvaluationRunClaimOutcome.LeaseActive,
                    null,
                    null
                )
            );
        await using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        var handler = new CodeReviewEvaluationRunRequestedHandler(
            Substitute.For<IDeadLetterWriter>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CodeReviewEvaluationRunRequestedHandler>.Instance
        );
        await handler.ExecuteAsync(
            new() { OrganizationId = "org", RunId = "run" },
            CancellationToken.None
        );
        await store
            .DidNotReceive()
            .FinalizeAsync(
                Arg.Any<CodeReviewEvaluationRunFinalize>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WorkerRunsTwoPipelinesAndStartsThirdOnlyAfterOneFinishes(int scenario)
    {
        var run = new CodeReviewEvaluationRun
        {
            Id = "run",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "key",
            State = CodeReviewEvaluationRunState.Queued,
        };
        var config = new CodeReviewerAgentRevisionConfiguration(
            "Reviewer",
            "facet",
            CodeReviewModelTier.High,
            "prompt",
            true,
            new()
        );
        var model = new CodeReviewEvaluationModelSelection(
            CodeReviewModelTier.High,
            "p",
            "m",
            null,
            CodeReviewEvaluationCredentialSource.SystemDefault,
            null
        );
        var cases = Enumerable
            .Range(1, 3)
            .Select(i => new CodeReviewEvaluationManifestCase(
                $"case{i}",
                i,
                $"PR {i}",
                new("uri", "hash", 1),
                "base",
                "head",
                DateTimeOffset.UtcNow,
                1,
                0,
                CodeReviewEvaluationReferenceSet.Unlabelled("agent")
            ))
            .ToArray();
        var manifest = new CodeReviewEvaluationRunManifest(
            1,
            "org",
            "repo",
            "dataset",
            1,
            "agent",
            "Reviewer",
            "facet",
            null,
            config,
            model,
            model,
            "1",
            "system",
            "hash",
            "{}",
            [],
            "build",
            "1",
            "user",
            "fingerprint",
            DateTimeOffset.UtcNow,
            null,
            cases
        );
        var store = Substitute.For<ICodeReviewEvaluationRunStore>();
        store
            .TryClaimAsync(
                "org",
                "run",
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationRunClaimResult(
                    CodeReviewEvaluationRunClaimOutcome.Claimed,
                    run,
                    manifest
                )
            );
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        store
            .RenewLeaseAsync(
                "org",
                "run",
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new CodeReviewEvaluationLeaseRenewal(scenario == 3, scenario == 3));
        results
            .ListForRunAsync("org", "run", Arg.Any<CancellationToken>())
            .Returns(
                cases
                    .Select(c => new CodeReviewEvaluationCaseResult
                    {
                        Id = c.CaseId,
                        OrganizationId = "org",
                        RunId = "run",
                        ManifestCaseId = c.CaseId,
                        PullRequestNumber = c.PullRequestNumber,
                        Phase = CodeReviewEvaluationCasePhase.Graded,
                        Outcome = CodeReviewEvaluationCaseOutcome.Graded,
                    })
                    .ToArray()
            );
        var barrier = new BarrierState { FailFirst = scenario == 1 };
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(results)
            .AddSingleton(barrier)
            .AddScoped<ICodeReviewEvaluationCaseExecutor, BarrierExecutor>()
            .BuildServiceProvider();
        await using (services)
        {
            var handler = new CodeReviewEvaluationRunRequestedHandler(
                Substitute.For<IDeadLetterWriter>(),
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CodeReviewEvaluationRunRequestedHandler>.Instance,
                clock
            );
            var work = handler.ExecuteAsync(
                new() { OrganizationId = "org", RunId = "run" },
                CancellationToken.None
            );
            await barrier.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(Volatile.Read(ref barrier.Started)).IsEqualTo(2);
            await Assert.That(Volatile.Read(ref barrier.Peak)).IsEqualTo(2);
            if (scenario is 2 or 3)
                clock.Advance(TimeSpan.FromSeconds(5));
            else
            {
                barrier.Release.Release();
                await barrier.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                barrier.Release.Release(2);
            }
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert
                .That(barrier.ScopeIds.Distinct().Count())
                .IsEqualTo(scenario is 2 or 3 ? 2 : 3);
            if (scenario == 2)
            {
                await store
                    .DidNotReceive()
                    .FinalizeAsync(
                        Arg.Any<CodeReviewEvaluationRunFinalize>(),
                        Arg.Any<CancellationToken>()
                    );
                return;
            }
            await store
                .Received(1)
                .FinalizeAsync(
                    Arg.Is<CodeReviewEvaluationRunFinalize>(x =>
                        x.State
                        == (
                            scenario == 3 ? CodeReviewEvaluationRunState.Cancelled
                            : scenario == 1 ? CodeReviewEvaluationRunState.Incomplete
                            : CodeReviewEvaluationRunState.Completed
                        )
                    ),
                    Arg.Any<CancellationToken>()
                );
        }
    }

    private sealed class BarrierState
    {
        public readonly SemaphoreSlim Release = new(0);
        public readonly TaskCompletionSource TwoStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public readonly TaskCompletionSource ThreeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public readonly System.Collections.Concurrent.ConcurrentBag<Guid> ScopeIds = [];
        public int Started;
        public int Active;
        public int Peak;
        public bool FailFirst;
    }

    private sealed class BarrierExecutor(BarrierState state) : ICodeReviewEvaluationCaseExecutor
    {
        private readonly Guid _scopeId = Guid.NewGuid();

        public async Task ExecuteAsync(
            CodeReviewEvaluationRun run,
            CodeReviewEvaluationRunManifest manifest,
            CodeReviewEvaluationManifestCase item,
            string leaseToken,
            CancellationToken cancellationToken
        )
        {
            state.ScopeIds.Add(_scopeId);
            var active = Interlocked.Increment(ref state.Active);
            int prior;
            do
            {
                prior = Volatile.Read(ref state.Peak);
                if (prior >= active)
                    break;
            } while (Interlocked.CompareExchange(ref state.Peak, active, prior) != prior);
            var count = Interlocked.Increment(ref state.Started);
            if (count == 2)
                state.TwoStarted.TrySetResult();
            if (count == 3)
                state.ThreeStarted.TrySetResult();
            await state.Release.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref state.Active);
            if (state.FailFirst && count == 1)
                throw new InvalidOperationException("Synthetic case persistence failure.");
        }
    }
}
