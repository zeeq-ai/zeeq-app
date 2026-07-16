using System.Runtime.CompilerServices;
using Zeeq.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests Agent Framework code-review workflow execution.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewWorkflowExecutorTests/*"
/// </summary>
public sealed class CodeReviewWorkflowExecutorTests
{
    private readonly CodeReviewXmlOutputValidator _xmlValidator = new();

    [Test]
    public async Task ReviewerValidatingExecutor_WithMalformedThenValidJson_RetriesAndReturnsValidBlock()
    {
        var runtimeAgent = RuntimeAgent("agent_structural", "Structural", "Structural Reviewer");
        var originalReviewPrompt = """
            Review this PR.

            <file_patch path="run.cs">
            +while (true) {
            +  x = x + 100_000_000;
            +}
            </file_patch>
            """;
        var malformedReviewOutput = """
            { "summary": "Potential infinite loop.", "details":
            """;
        var chatClient = new ScriptedChatClient(
            malformedReviewOutput,
            ReviewBlock("Corrected summary")
        );
        var executor = new CodeReviewReviewerValidatingExecutor(
            "validator_structural",
            CreateAgent(runtimeAgent, chatClient),
            runtimeAgent,
            _xmlValidator
        );

        var result = await executor.HandleAsync(
            new ChatMessage(ChatRole.User, originalReviewPrompt),
            Substitute.For<IWorkflowContext>(),
            CancellationToken.None
        );
        var validation = _xmlValidator.ValidateReviewerBlock(result.Text);
        var correctionPrompt = JoinedMessageText(chatClient.Calls[1]);

        await Assert.That(chatClient.CallCount).IsEqualTo(2);
        await Assert.That(correctionPrompt).Contains("<original_review_request>");
        await Assert.That(correctionPrompt).Contains("<file_patch path=\"run.cs\">");
        await Assert.That(correctionPrompt).Contains("+while (true) {");
        await Assert.That(correctionPrompt).Contains("<previous_invalid_response>");
        await Assert.That(correctionPrompt).Contains(malformedReviewOutput);
        await Assert.That(correctionPrompt).Contains("do not state that no code diff was supplied");
        await Assert.That(correctionPrompt).Contains("single JSON object");
        await Assert
            .That(correctionPrompt)
            .Contains("Do NOT include \"facet\" or \"agent\" fields");
        await Assert.That(result.AuthorName).IsEqualTo("Structural");
        await Assert.That(validation.IsValid).IsTrue();
        // The reviewer emits JSON; the executor re-serializes to the canonical XML block, so
        // the facet/agent identity is stamped from the runtime agent, not the model output.
        var review = validation.Output!.Reviews.Single();
        await Assert.That(review.Facet).IsEqualTo("Structural");
        await Assert.That(review.Agent).IsEqualTo("Structural Reviewer");
        await Assert.That(review.Summary).IsEqualTo("Corrected summary");
    }

    [Test]
    public async Task ReviewerValidatingExecutor_WhenRetriesExhausted_ReturnsFailedReviewerBlock()
    {
        var runtimeAgent = RuntimeAgent("agent_tests", "Test", "Test Reviewer");
        var chatClient = new ScriptedChatClient("nope", "still not json", "and still not json");
        var executor = new CodeReviewReviewerValidatingExecutor(
            "validator_test",
            CreateAgent(runtimeAgent, chatClient),
            runtimeAgent,
            _xmlValidator
        );

        var result = await executor.HandleAsync(
            new ChatMessage(ChatRole.User, "Review this PR."),
            Substitute.For<IWorkflowContext>(),
            CancellationToken.None
        );
        var validation = _xmlValidator.ValidateReviewerBlock(result.Text);
        var review = validation.Output!.Reviews.Single();
        var finding = review.Findings.Single();

        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(result.AuthorName).IsEqualTo("Test");
        await Assert.That(review.Facet).IsEqualTo("Test");
        await Assert.That(review.Agent).IsEqualTo("Test Reviewer");
        await Assert.That(review.Summary).IsEqualTo("Reviewer output could not be validated.");
        await Assert.That(finding.File).IsEqualTo("(reviewer-output)");
        await Assert.That(finding.Summary).IsEqualTo("Reviewer output failed validation");
        await Assert.That(finding.Details).Contains("did not contain a JSON object");
        await Assert.That(chatClient.CallCount).IsEqualTo(3);
    }

    [Test]
    public async Task AgentExecutorExecuteWorkflowAsync_WithMultipleReviewers_AggregatesCanonicalXml()
    {
        var executor = CreateExecutor();
        var reviewers = new[]
        {
            WorkflowReviewer(
                RuntimeAgent("agent_structural", "Structural", "Structural Reviewer"),
                new ScriptedChatClient(ReviewBlock("Structural summary"))
            ),
            WorkflowReviewer(
                RuntimeAgent("agent_performance", "Performance", "Performance Reviewer"),
                new ScriptedChatClient(ReviewBlock("Performance summary"))
            ),
        };

        var xml = await executor.ExecuteWorkflowAsync(
            reviewers,
            "Review this PR.",
            CancellationToken.None
        );
        var validation = _xmlValidator.Validate(xml);

        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(validation.Output!.NoAgentsActivated).IsFalse();
        await Assert.That(validation.Output.Reviews).Count().IsEqualTo(2);
        await Assert
            .That(validation.Output.Reviews.Select(review => review.Facet))
            .IsEquivalentTo(["Structural", "Performance"]);
    }

    [Test]
    public async Task AgentExecutorExecuteWorkflowAsync_WithOneInvalidReviewer_AggregatesFailureBlock()
    {
        var executor = CreateExecutor();
        var reviewers = new[]
        {
            WorkflowReviewer(
                RuntimeAgent("agent_structural", "Structural", "Structural Reviewer"),
                new ScriptedChatClient(ReviewBlock("Structural summary"))
            ),
            WorkflowReviewer(
                RuntimeAgent("agent_tests", "Test", "Test Reviewer"),
                new ScriptedChatClient("nope", "still", "not json either")
            ),
        };

        var xml = await executor.ExecuteWorkflowAsync(
            reviewers,
            "Review this PR.",
            CancellationToken.None
        );
        var validation = _xmlValidator.Validate(xml);
        var structural = validation.Output!.Reviews.Single(review => review.Facet == "Structural");
        var test = validation.Output.Reviews.Single(review => review.Facet == "Test");

        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(validation.Output.Reviews).Count().IsEqualTo(2);
        await Assert.That(structural.Summary).IsEqualTo("Structural summary");
        await Assert.That(test.Summary).IsEqualTo("Reviewer output could not be validated.");
        await Assert.That(test.Findings.Single().Details).Contains("did not contain a JSON object");
    }

    [Test]
    public async Task AgentExecutorValidateAndSerializeAggregateBlocks_WithEmptyAggregate_ThrowsClearInvariant()
    {
        var executor = CreateExecutor();

        await Assert
            .That(() => executor.ValidateAndSerializeAggregateBlocks(""))
            .Throws<InvalidOperationException>()
            .WithMessage("Code-review workflow completed without any reviewer outputs.");
    }

    [Test]
    public async Task AgentExecutorExecuteWorkflowAsync_StartsReviewersConcurrently()
    {
        var executor = CreateExecutor();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstAgent = RuntimeAgent("agent_structural", "Structural", "Structural Reviewer");
        var secondAgent = RuntimeAgent("agent_tests", "Test", "Test Reviewer");
        var firstClient = new ScriptedChatClient(
            async (_, cancellationToken) =>
            {
                firstStarted.TrySetResult();
                await secondStarted.Task.WaitAsync(cancellationToken);
                await releaseFirst.Task.WaitAsync(cancellationToken);

                return ReviewBlock("Structural summary");
            }
        );
        var secondClient = new ScriptedChatClient(
            (_, _) =>
            {
                secondStarted.TrySetResult();

                return Task.FromResult(ReviewBlock("Test summary"));
            }
        );
        var reviewers = new[]
        {
            WorkflowReviewer(firstAgent, firstClient),
            WorkflowReviewer(secondAgent, secondClient),
        };

        var execution = executor.ExecuteWorkflowAsync(
            reviewers,
            "Review this PR.",
            CancellationToken.None
        );
        await Task.WhenAll(
            firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5))
        );
        releaseFirst.SetResult();

        var xml = await execution;
        var validation = _xmlValidator.Validate(xml);
        var orderedFacets = string.Join(
            ",",
            validation.Output!.Reviews.Select(review => review.Facet)
        );

        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(orderedFacets).IsEqualTo("Structural,Test");
        await Assert.That(firstClient.CallCount).IsEqualTo(1);
        await Assert.That(secondClient.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task AgentExecutorExecuteAsync_WithNoAgentsActivated_ReturnsEmptyNoAgentsDocument()
    {
        var executor = CreateExecutor();

        var xml = await executor.ExecuteAsync(
            "org_123",
            [],
            noAgentsActivated: true,
            new CodeReviewUserPrompt("Review this PR."),
            previousReviews: [],
            callerIdentity: new System.Security.Claims.ClaimsPrincipal(),
            telemetry: new CodeReviewTelemetryContext(),
            CancellationToken.None
        );
        var validation = _xmlValidator.Validate(xml);

        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(validation.Output!.NoAgentsActivated).IsTrue();
        await Assert.That(validation.Output.Reviews).IsEmpty();
    }

    private CodeReviewAgentExecutor CreateExecutor() =>
        new(
            null!,
            new(_xmlValidator),
            _xmlValidator,
            NullLoggerFactory.Instance,
            EmptyServiceProvider.Instance
        );

    private static CodeReviewWorkflowReviewer WorkflowReviewer(
        CodeReviewerRuntimeAgent runtimeAgent,
        ScriptedChatClient chatClient
    ) =>
        new(
            runtimeAgent,
            CreateAgent(runtimeAgent, chatClient),
            Provider: string.Empty,
            Model: string.Empty
        );

    private static AIAgent CreateAgent(
        CodeReviewerRuntimeAgent runtimeAgent,
        IChatClient chatClient
    ) =>
        chatClient.AsAIAgent(
            instructions: runtimeAgent.Prompt,
            name: runtimeAgent.DisplayName,
            description: $"Test code-review agent for {runtimeAgent.ReviewFacet}.",
            tools: null,
            loggerFactory: NullLoggerFactory.Instance,
            services: EmptyServiceProvider.Instance
        );

    private static CodeReviewerRuntimeAgent RuntimeAgent(
        string id,
        string facet,
        string displayName
    ) =>
        new(
            id,
            displayName,
            facet,
            CodeReviewModelTier.High,
            $"Review the PR for {facet}.",
            CodeReviewerActivationConfiguration.Empty
        );

    // Reviewer output is now JSON (facet/agent are stamped from the runtime agent downstream).
    private static string ReviewBlock(string summary) =>
        $$"""
            {
              "summary": "{{summary}}",
              "details": "details",
              "findings": []
            }
            """;

    private static string JoinedMessageText(IReadOnlyList<ChatMessage> messages) =>
        string.Join(Environment.NewLine, messages.Select(message => message.Text));

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<
            Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>
        > _responses = [];

        public ScriptedChatClient(params string[] responses)
        {
            foreach (var response in responses)
            {
                _responses.Enqueue((_, _) => Task.FromResult(response));
            }
        }

        public ScriptedChatClient(
            params Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>[] responses
        )
        {
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public int CallCount => Calls.Count;

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var snapshot = messages.ToArray();
            Calls.Add(snapshot);

            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No scripted chat response was available.");
            }

            var text = await response(snapshot, cancellationToken);

            return new(new ChatMessage(ChatRole.Assistant, text));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);

            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
