using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Core.Llm.Tests;

/// <summary>
/// Unit tests for bounded provider access testing.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Llm.Tests --output detailed --disable-logo --treenode-filter "/*/*/LlmProviderAccessTesterTests/*"
/// </summary>
public sealed class LlmProviderAccessTesterTests
{
    [Test]
    public async Task LlmProviderAccessTester_TestAsync_WithSuccessfulCall_ReturnsSanitizedSuccess()
    {
        var chatClient = new FakeChatClient { ResponseText = "raw provider generated text" };
        var tester = new LlmProviderAccessTester(
            new FakeLlmClientFactory(chatClient),
            new LlmProviderAccessTestOptions(),
            NullLogger<LlmProviderAccessTester>.Instance
        );

        var result = await tester.TestAsync(
            Configuration(),
            prompt: "  Reply with OK.  ",
            CancellationToken.None
        );

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ErrorCode).IsNull();
        await Assert.That(result.Message).IsEqualTo("Provider access test completed.");
        await Assert.That(result.Message).DoesNotContain("raw provider generated text");
        await Assert.That(chatClient.LastPrompt).IsEqualTo("Reply with OK.");
        await Assert.That(chatClient.LastOptions?.MaxOutputTokens).IsEqualTo(16);
        await Assert.That(chatClient.LastOptions?.Temperature).IsEqualTo(0);
    }

    [Test]
    public async Task LlmProviderAccessTester_TestAsync_WithTooLongPrompt_ReturnsInvalidPrompt()
    {
        var chatClient = new FakeChatClient();
        var tester = new LlmProviderAccessTester(
            new FakeLlmClientFactory(chatClient),
            new LlmProviderAccessTestOptions { MaxPromptLength = 5 },
            NullLogger<LlmProviderAccessTester>.Instance
        );

        var result = await tester.TestAsync(
            Configuration(),
            prompt: "too long",
            CancellationToken.None
        );

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_prompt");
        await Assert.That(chatClient.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task LlmProviderAccessTester_TestAsync_WithUnsupportedProvider_ReturnsSanitizedFailure()
    {
        var tester = new LlmProviderAccessTester(
            new FakeLlmClientFactory(new FakeChatClient())
            {
                CreateException = new NotSupportedException("raw unsupported detail"),
            },
            new LlmProviderAccessTestOptions(),
            NullLogger<LlmProviderAccessTester>.Instance
        );

        var result = await tester.TestAsync(
            Configuration(provider: "Anthropic"),
            prompt: null,
            CancellationToken.None
        );

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Provider).IsEqualTo("Anthropic");
        await Assert.That(result.ErrorCode).IsEqualTo("unsupported_provider");
        await Assert.That(result.Message).DoesNotContain("raw unsupported detail");
    }

    [Test]
    public async Task LlmProviderAccessTester_TestAsync_WithProviderException_ReturnsSanitizedFailure()
    {
        var tester = new LlmProviderAccessTester(
            new FakeLlmClientFactory(
                new FakeChatClient
                {
                    ResponseException = new InvalidOperationException("raw provider body"),
                }
            ),
            new LlmProviderAccessTestOptions(),
            NullLogger<LlmProviderAccessTester>.Instance
        );

        var result = await tester.TestAsync(Configuration(), prompt: null, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("provider_error");
        await Assert.That(result.Message).DoesNotContain("raw provider body");
    }

    [Test]
    public async Task LlmClientFactory_CreateAgent_ReturnsChatClientAgent()
    {
        var factory = new LlmClientFactory(
            EmptyServiceProvider.Instance,
            NullLoggerFactory.Instance
        );

        var agent = factory.CreateAgent(Configuration());

        await Assert.That(agent).IsNotNull();
        await Assert.That(agent).IsAssignableTo<AIAgent>();
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaTools_RemovesReasoningOptions()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("gpt-5.6-luna", options);

        await Assert.That(options.Reasoning).IsNull();
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaToolsAndNoReasoning_PreservesNullReasoning()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("gpt-5.6-luna", options);

        await Assert.That(options.Reasoning).IsNull();
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaAndNoTools_PreservesReasoningEffort()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("gpt-5.6-luna", options);

        await Assert.That(options.Reasoning?.Effort).IsEqualTo(ReasoningEffort.High);
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithNonLunaTools_PreservesReasoningEffort()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("gpt-5.6-sol", options);

        await Assert.That(options.Reasoning?.Effort).IsEqualTo(ReasoningEffort.High);
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaTemperatureZero_RewritesTemperature()
    {
        var options = new ChatOptions { Temperature = 0 };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("gpt-5.6-luna", options);

        await Assert.That(options.Temperature).IsEqualTo(1);
    }

    private static ResolvedLlmConfiguration Configuration(
        string provider = "OpenAI",
        string model = "gpt-test"
    ) => new(provider, model, ApiKey: "test-api-key", KeySource: "tenant-key");

    private sealed class FakeLlmClientFactory(FakeChatClient chatClient) : ILlmClientFactory
    {
        public Exception? CreateException { get; init; }

        public IChatClient CreateChatClient(ResolvedLlmConfiguration configuration)
        {
            if (CreateException is not null)
            {
                throw CreateException;
            }

            return chatClient;
        }

        public IChatClient CreateDefaultChatClient(
            Zeeq.Core.Common.LlmModelDefault configuration
        ) => chatClient;

        public AIAgent CreateAgent(ResolvedLlmConfiguration configuration) =>
            throw new NotImplementedException();

        public IEmbeddingGenerator<string, Embedding<float>> CreateDefaultEmbeddingGenerator(
            Zeeq.Core.Common.LlmEmbeddingSettings settings,
            EmbeddingClientProfile profile
        ) => throw new NotImplementedException();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public string? LastPrompt { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public string ResponseText { get; init; } = "OK";

        public Exception? ResponseException { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ResponseException is not null)
            {
                throw ResponseException;
            }

            CallCount++;
            LastPrompt = messages.Single().Text;
            LastOptions = options;

            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText))
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
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
