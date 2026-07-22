using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OpenAIChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;

namespace Zeeq.Core.Llm.Tests;

/*
Verification notes for the GPT-5.6 tool-call compatibility shim:

These unit tests protect the option-normalization surface, but the behavior that
matters is the fully wired runtime client because MEAI's OpenAI adapter combines
ChatOptions, RawRepresentationFactory, and function tools immediately before the
provider SDK serializes the request. To verify end-to-end:

1. Rebuild the local server resource with Aspire:
   aspire resource zeeq-server rebuild --non-interactive
   aspire wait zeeq-server --non-interactive

2. Attach CSharpRepl to the `Zeeq.Runtime.Server` process:
   csharprepl connect list
   NO_COLOR=1 csharprepl connect <PID> --eval '<probe>'

3. In the probe, resolve real services from DI (`PostgresDbContext`,
   `ILlmClientFactory`, and `KeyEncryptionService`), decrypt the organization
   managed OpenAI/Azure OpenAI keys, then call `factory.CreateChatClient(...)`.
   Build a `ChatOptions` with:
   - a function tool, for example `AIFunctionFactory.Create(() => "ok", name: "test_tool")`
   - a `RawRepresentationFactory` that returns `OpenAI.Chat.ChatCompletionOptions`
     with `ReasoningEffortLevel = High`

4. Send a small prompt such as "Call test_tool, then reply OK." Expected result:
   Azure OpenAI `gpt-5.6-luna` succeeds because the shim omits raw
   `reasoning_effort`; native OpenAI `gpt-5.6-luna`, `gpt-5.6-sol`, and
   `gpt-5.6-terra` succeed because the shim sends `reasoning_effort=none`.
   Also run a no-tool `Temperature = 0` probe for `gpt-5.5` and the GPT-5.6
   models; the shim should rewrite temperature to the provider default and the
   call should complete.

This runtime probe caught provider differences that unit tests alone did not:
native OpenAI accepts explicit `none`, while Azure OpenAI rejects any serialized
`reasoning_effort` value for GPT-5.6 function-tool calls on Chat Completions.
*/

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
    public async Task NormalizeOpenAiChatCompletionsOptions_WithGpt56Tools_RemovesReasoningOptions()
    {
        foreach (var model in new[] { "gpt-5.6-luna", "gpt-5.6-sol", "gpt-5.6-terra" })
        {
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
            };

            LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", model, options);

            await Assert.That(options.Reasoning).IsNull();
        }
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithOpenAiGpt56Tools_SetsRawReasoningEffortToNone()
    {
        foreach (var model in new[] { "gpt-5.6-luna", "gpt-5.6-sol", "gpt-5.6-terra" })
        {
            var options = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
                RawRepresentationFactory = _ =>
#pragma warning disable OPENAI001
                    new OpenAIChatCompletionOptions
                    {
                        ReasoningEffortLevel = OpenAIChatReasoningEffortLevel.High,
                    },
#pragma warning restore OPENAI001
            };

            LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", model, options);

            var rawOptions = (OpenAIChatCompletionOptions)options.RawRepresentationFactory!(null!)!;
#pragma warning disable OPENAI001
            await Assert.That(rawOptions.ReasoningEffortLevel).IsEqualTo(
                OpenAIChatReasoningEffortLevel.None
            );
#pragma warning restore OPENAI001
        }
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithAzureGpt56Tools_RemovesRawReasoningEffort()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
            RawRepresentationFactory = _ =>
#pragma warning disable OPENAI001
                new OpenAIChatCompletionOptions
                {
                    ReasoningEffortLevel = OpenAIChatReasoningEffortLevel.High,
                },
#pragma warning restore OPENAI001
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions(
            "Azure OpenAI",
            "gpt-5.6-luna",
            options
        );

        var rawOptions = (OpenAIChatCompletionOptions)options.RawRepresentationFactory!(null!)!;
#pragma warning disable OPENAI001
        await Assert.That(rawOptions.ReasoningEffortLevel.HasValue).IsFalse();
#pragma warning restore OPENAI001
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithReusedOptions_DoesNotStackRawFactoryWrappers()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
            RawRepresentationFactory = _ =>
#pragma warning disable OPENAI001
                new OpenAIChatCompletionOptions
                {
                    ReasoningEffortLevel = OpenAIChatReasoningEffortLevel.High,
                },
#pragma warning restore OPENAI001
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions(
            "Azure OpenAI",
            "gpt-5.6-luna",
            options
        );
        var wrappedFactory = options.RawRepresentationFactory;

        var azureRawOptions = (OpenAIChatCompletionOptions)wrappedFactory!(null!)!;
#pragma warning disable OPENAI001
        await Assert.That(azureRawOptions.ReasoningEffortLevel.HasValue).IsFalse();
#pragma warning restore OPENAI001

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions(
            "OpenAI",
            "gpt-5.6-luna",
            options
        );

        await Assert.That(ReferenceEquals(wrappedFactory, options.RawRepresentationFactory))
            .IsTrue();

        var openAiRawOptions = (OpenAIChatCompletionOptions)options.RawRepresentationFactory!(null!)!;
#pragma warning disable OPENAI001
        await Assert.That(openAiRawOptions.ReasoningEffortLevel).IsEqualTo(
            OpenAIChatReasoningEffortLevel.None
        );
#pragma warning restore OPENAI001
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaToolsAndNoReasoning_PreservesNullReasoning()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", "gpt-5.6-luna", options);

        await Assert.That(options.Reasoning).IsNull();
        var rawOptions = (OpenAIChatCompletionOptions)options.RawRepresentationFactory!(null!)!;
#pragma warning disable OPENAI001
        await Assert.That(rawOptions.ReasoningEffortLevel).IsEqualTo(
            OpenAIChatReasoningEffortLevel.None
        );
#pragma warning restore OPENAI001
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithLunaAndNoTools_PreservesReasoningEffort()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", "gpt-5.6-luna", options);

        await Assert.That(options.Reasoning?.Effort).IsEqualTo(ReasoningEffort.High);
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithNonGpt56Tools_PreservesReasoningEffort()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            Tools = [AIFunctionFactory.Create(() => "ok", name: "test_tool")],
        };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", "gpt-5.5", options);

        await Assert.That(options.Reasoning?.Effort).IsEqualTo(ReasoningEffort.High);
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithUnsupportedTemperatureZeroModels_RewritesTemperature()
    {
        foreach (var model in new[] { "gpt-5.5", "gpt-5.6-luna", "gpt-5.6-sol", "gpt-5.6-terra" })
        {
            var options = new ChatOptions { Temperature = 0 };

            LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", model, options);

            await Assert.That(options.Temperature).IsEqualTo(1);
        }
    }

    [Test]
    public async Task NormalizeOpenAiChatCompletionsOptions_WithSupportedTemperatureZeroModel_PreservesTemperature()
    {
        var options = new ChatOptions { Temperature = 0 };

        LlmClientFactory.NormalizeOpenAiChatCompletionsOptions("OpenAI", "gpt-5.4", options);

        await Assert.That(options.Temperature).IsEqualTo(0);
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
