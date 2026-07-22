using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using Zeeq.Core.Common;

namespace Zeeq.Core.Llm;

/// <summary>
/// Creates Microsoft.Extensions.AI chat clients and Agent Framework agents for LLM calls.
/// </summary>
/// <remarks>
/// Zeeq has two credential paths: app-level default credentials from
/// configuration and tenant-owned credentials decrypted immediately before a
/// workflow runs. Both paths use the same construction pipeline so provider
/// options, timeout behavior, function invocation, and OpenTelemetry wiring
/// stay consistent.
///
/// The factory stores only non-secret dependencies. API keys are passed into
/// factory methods and captured only by the provider SDK client created for
/// that call. Tenant-owned keys are not registered in DI, stored on singleton
/// services, logged, traced, or used as cache keys.
/// </remarks>
public sealed class LlmClientFactory(IServiceProvider services, ILoggerFactory loggerFactory)
    : ILlmClientFactory
{
    /// <summary>
    /// Timeout applied to all provider SDK clients.
    /// </summary>
    /// <remarks>
    /// Long-context model calls can exceed the .NET default 100 second
    /// <see cref="HttpClient" /> timeout. Keep this aligned with the legacy
    /// Zeeq OpenAI client factory so slow provider calls are governed by
    /// Zeeq's operation-level limits rather than the transport default.
    /// </remarks>
    private static readonly TimeSpan ChatClientTimeout = TimeSpan.FromSeconds(300);

    private const string RawReasoningEffortFactoryStateKey =
        "Zeeq.Core.Llm.OpenAiRawReasoningEffortFactoryState";

    private static readonly HttpClient SharedHttpClient = new() { Timeout = ChatClientTimeout };

    private static readonly HttpClientPipelineTransport Transport = new(SharedHttpClient);

    /// <summary>
    /// Creates a chat client for one resolved tenant-owned credential.
    /// </summary>
    /// <remarks>
    /// Callers should resolve organization tier settings, key ownership, and
    /// decrypted API key material before calling this method. The returned
    /// client is intended for the current request or workflow run; it should not
    /// be cached globally because it captures tenant credential material inside
    /// the provider SDK client.
    /// </remarks>
    public IChatClient CreateChatClient(ResolvedLlmConfiguration configuration)
    {
        return CreateChatClient(
            provider: configuration.Provider,
            model: configuration.Model,
            apiKey: configuration.ApiKey,
            endpoint: configuration.Endpoint
        );
    }

    /// <summary>
    /// Creates a chat client for an app-level default tier credential.
    /// </summary>
    /// <remarks>
    /// Default clients are used only when a tier selects Zeeq's internal
    /// configured key instead of a tenant-owned encrypted key. The same pipeline
    /// is used as tenant clients so telemetry and provider behavior stay
    /// consistent, but these clients are registered as scoped keyed services by
    /// <see cref="SetupLlm" /> because their credentials come from application
    /// configuration rather than tenant storage.
    /// </remarks>
    public IChatClient CreateDefaultChatClient(LlmModelDefault configuration)
    {
        return CreateChatClient(
            provider: configuration.Provider,
            model: configuration.Model,
            apiKey: configuration.ApiKey,
            endpoint: configuration.Endpoint
        );
    }

    /// <summary>
    /// Creates a Microsoft Agent Framework agent for one resolved tenant credential.
    /// </summary>
    /// <remarks>
    /// Agent Framework agents also capture the chat client they wrap, so this
    /// method follows the same per-run rule as
    /// <see cref="CreateChatClient(ResolvedLlmConfiguration)" />:
    /// construct the agent after resolving the organization, tier, provider,
    /// model, and API key, then let the caller own the agent lifetime for that
    /// workflow. The agent is intentionally created from the same chat-client
    /// pipeline so OpenTelemetry and middleware behavior match direct chat
    /// usage.
    /// </remarks>
    public AIAgent CreateAgent(ResolvedLlmConfiguration configuration)
    {
        return CreateChatClient(configuration)
            .AsAIAgent(
                instructions: null,
                name: "Zeeq LLM Agent",
                description: "Per-run tenant LLM agent.",
                tools: null,
                loggerFactory: loggerFactory,
                services: services
            )
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: LlmTelemetry.ActivitySourceName,
                configure: config =>
                {
                    config.EnableSensitiveData = true; // TODO: This should be only on local
                }
            )
            .Build(services);
    }

    /// <summary>
    /// Creates a platform-level default embedding generator for snippet indexing/search.
    /// </summary>
    /// <remarks>
    /// Same construction pipeline as <see cref="CreateChatClient(ResolvedLlmConfiguration)" />'s
    /// OpenAI-compatible branch — shared <see cref="HttpClient" />/transport, MEAI adapter,
    /// <c>UseOpenTelemetry</c> middleware — so embedding calls appear in the same OTEL source as
    /// chat/agent calls. Retry and per-attempt timeout are entirely SDK-native
    /// (<see cref="ClientRetryPolicy" />), tuned per <paramref name="profile" /> rather than by a
    /// custom middleware layer: empirically verified (loopback stub, 2026-07-11) that
    /// <see cref="ClientRetryPolicy" /> honors a <c>Retry-After</c> response header exactly and
    /// does not retry non-transient (4xx) statuses, so no custom retry/backoff code is needed here.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>> CreateDefaultEmbeddingGenerator(
        LlmEmbeddingSettings settings,
        EmbeddingClientProfile profile
    )
    {
        var (maxRetries, networkTimeout) = profile switch
        {
            // Batch (sweep): a tick already tolerates delay; retry hard against transient
            // 429/5xx before giving up and releasing the embedding lease for the next tick.
            EmbeddingClientProfile.Batch => (5, TimeSpan.FromSeconds(30)),
            // Query (interactive search): near-zero retry budget — SnippetSearchService's own
            // short CancellationToken is the real backstop; any failure degrades to full-text
            // search rather than making a caller wait out a multi-second backoff.
            EmbeddingClientProfile.Query => (0, TimeSpan.FromSeconds(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

        var options = new OpenAIClientOptions
        {
            NetworkTimeout = networkTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries),
            Transport = Transport,
            Endpoint = new Uri(settings.Endpoint, UriKind.Absolute),
        };

        return new OpenAIClient(new ApiKeyCredential(settings.ApiKey.Trim()), options)
            .GetEmbeddingClient(settings.Model.Trim())
            .AsIEmbeddingGenerator()
            .AsBuilder()
            .UseOpenTelemetry(loggerFactory, sourceName: LlmTelemetry.ActivitySourceName)
            .Build(services);
    }

    /// <summary>
    /// Builds the concrete provider SDK client and wraps it as an
    /// <see cref="IChatClient" /> with Zeeq's common middleware.
    /// </summary>
    /// <remarks>
    /// Two provider paths are supported:
    /// <list type="bullet">
    ///   <item>
    ///     <term>Anthropic</term>
    ///     <description>
    ///       Uses the Anthropic SDK's <see cref="AnthropicClient" /> and its MEAI
    ///       <c>AsIChatClient</c> extension. The <paramref name="endpoint" /> parameter
    ///       sets a custom base URL (e.g. for Anthropic-compatible proxies); omit it to
    ///       use the default <c>https://api.anthropic.com</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Fireworks / OpenAI</term>
    ///     <description>
    ///       Uses the OpenAI SDK. Fireworks supplies its endpoint
    ///       (<c>https://api.fireworks.ai/inference/v1</c>); OpenAI uses the SDK default.
    ///     </description>
    ///   </item>
    /// </list>
    /// The provider SDK receives the trimmed API key only at construction time.
    /// </remarks>
    private IChatClient CreateChatClient(
        string provider,
        string model,
        string apiKey,
        string endpoint
    )
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("LLM model is not configured.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("LLM API key is not configured.");
        }

        IChatClient rawClient;

        if (IsAnthropicProvider(provider))
        {
            // See: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentProviders/anthropic/Agent_Anthropic_Step01_Running/Program.cs
            // See: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentProviders/anthropic/Agent_Anthropic_Step03_UsingFunctionTools/Program.cs.
            var clientOptions = new ClientOptions
            {
                ApiKey = apiKey.Trim(),
                HttpClient = SharedHttpClient,
            };
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                clientOptions.BaseUrl = endpoint.Trim();
            }

            rawClient = new AnthropicClient(clientOptions).AsIChatClient(model.Trim());
        }
        else if (IsAzureOpenAiProvider(provider))
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException("Azure OpenAI requires an endpoint URL.");
            }

            rawClient = new AzureOpenAIClient(
                new Uri(endpoint.Trim(), UriKind.Absolute),
                new AzureKeyCredential(apiKey.Trim())
            )
                .GetChatClient(model.Trim())
                .AsIChatClient();
        }
        else if (IsOpenAiCompatibleProvider(provider))
        {
            var options = new OpenAIClientOptions
            {
                NetworkTimeout = ChatClientTimeout,
                Transport = Transport,
            };

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                options.Endpoint = new Uri(endpoint, UriKind.Absolute);
            }

            rawClient = new OpenAIClient(new ApiKeyCredential(apiKey.Trim()), options)
                .GetChatClient(model.Trim())
                .AsIChatClient();
        }
        else
        {
            throw new NotSupportedException(
                $"LLM provider '{provider}' is not wired for chat client creation yet."
            );
        }

        return rawClient
            .AsBuilder()
            .UseFunctionInvocation(
                loggerFactory,
                configure: config =>
                {
                    config.AllowConcurrentInvocation = true;
                }
            )
            // Usage accumulator, registered BELOW function invocation so it observes every
            // LLM round trip of a run (the tool-calling loop makes several). It sums each
            // round trip's usage into a per-run LlmUsageSink when one is threaded through
            // ChatOptions.AdditionalProperties; a no-op for every other caller. This is the
            // only accurate source of per-run token totals — neither AgentRunResponse.Usage
            // nor the post-function-invocation ChatResponse.Usage aggregates across round
            // trips (Phase 0 spike). See LlmUsageSink. Both the response and streaming paths
            // accumulate so any future streaming caller that threads a sink is also counted.
            .Use(
                getResponseFunc: async (messages, options, innerClient, cancellationToken) =>
                {
                    var response = await innerClient.GetResponseAsync(
                        messages,
                        options,
                        cancellationToken
                    );

                    LlmUsageSink.Resolve(options)?.Add(response.Usage);

                    return response;
                },
                getStreamingResponseFunc: AccumulateStreamingUsageAsync
            )
            .UseOpenTelemetry(
                loggerFactory,
                sourceName: LlmTelemetry.ActivitySourceName,
                configure: config =>
                {
                    config.EnableSensitiveData = true; // TODO: This should be only on local
                }
            )
            // OpenAI Chat Completions compatibility. GPT-5.6 models work with
            // function tools on Chat Completions only when reasoning_effort is
            // absent, so Zeeq intentionally drops explicit reasoning for those
            // tool-call requests instead of routing native OpenAI through
            // Responses and giving Azure a different behavior. GPT-5.5 and
            // GPT-5.6 also reject Temperature = 0. This innermost middleware
            // rewrites the MEAI ChatOptions immediately before the provider SDK
            // sees the request.
            .Use(
                getResponseFunc: (messages, options, innerClient, cancellationToken) =>
                {
                    NormalizeOpenAiChatCompletionsOptions(provider, model, options);

                    return innerClient.GetResponseAsync(messages, options, cancellationToken);
                },
                getStreamingResponseFunc: (messages, options, innerClient, cancellationToken) =>
                {
                    NormalizeOpenAiChatCompletionsOptions(provider, model, options);

                    return innerClient.GetStreamingResponseAsync(
                        messages,
                        options,
                        cancellationToken
                    );
                }
            )
            .Build(services);
    }

    internal static void NormalizeOpenAiChatCompletionsOptions(
        string provider,
        string model,
        ChatOptions? options
    )
    {
        if (options is null)
        {
            return;
        }

        if (options.Temperature == 0 && IsTemperatureZeroUnsupportedModel(model))
        {
            options.Temperature = 1;
        }

        if (
            options.Tools is { Count: > 0 }
            && IsReasoningWithToolsUnsupportedModel(model)
        )
        {
            // NOTE: We intentionally trade explicit reasoning effort for one
            // Chat Completions code path across OpenAI and Azure OpenAI.
            // GPT-5.6 rejects reasoning_effort with function tools on this
            // endpoint, so keeping tools reliable means removing MEAI reasoning
            // before the provider SDK serializes the request.
            options.Reasoning = null;
            ClearRawOpenAiChatReasoningEffort(
                options,
                useExplicitNone: IsOpenAiProvider(provider)
            );
        }
    }

    private static void ClearRawOpenAiChatReasoningEffort(
        ChatOptions options,
        bool useExplicitNone
    )
    {
        var properties = options.AdditionalProperties ??= [];
        if (
            properties.TryGetValue(RawReasoningEffortFactoryStateKey, out var existingState)
            && existingState is RawReasoningEffortFactoryState state
        )
        {
            state.UseExplicitNone = useExplicitNone;

            return;
        }

        state = new RawReasoningEffortFactoryState(
            options.RawRepresentationFactory,
            useExplicitNone
        );
        properties[RawReasoningEffortFactoryStateKey] = state;
        options.RawRepresentationFactory = state.CreateRawRepresentation;
    }

    private sealed class RawReasoningEffortFactoryState(
        Func<IChatClient, object?>? originalFactory,
        bool useExplicitNone
    )
    {
        public bool UseExplicitNone { get; set; } = useExplicitNone;

        public object? CreateRawRepresentation(IChatClient client)
        {
            var rawRepresentation = originalFactory?.Invoke(client);
            if (rawRepresentation is OpenAIChatCompletionOptions rawOptions)
            {
                ApplyReasoningEffortCompatibility(rawOptions);
            }
            else if (rawRepresentation is null && UseExplicitNone)
            {
#pragma warning disable OPENAI001
                rawRepresentation = new OpenAIChatCompletionOptions
                {
                    ReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel.None,
                };
#pragma warning restore OPENAI001
            }

            return rawRepresentation;
        }

        private void ApplyReasoningEffortCompatibility(OpenAIChatCompletionOptions rawOptions)
        {
            // MEAI OpenAIChatClient.ToOpenAIOptions first asks
            // ChatOptions.RawRepresentationFactory for a raw OpenAI
            // ChatCompletionOptions and only maps ChatOptions.Reasoning when
            // that raw options object has no ReasoningEffortLevel. Clearing
            // ChatOptions.Reasoning alone therefore does not remove an already
            // populated raw ReasoningEffortLevel. Native OpenAI GPT-5.6
            // requires the explicit value `none`, while Azure OpenAI rejects
            // any reasoning_effort value on Chat Completions with tools.
            // Keep that provider split local to the compatibility shim so
            // both providers still use the same Chat Completions client path.
#pragma warning disable OPENAI001
            if (UseExplicitNone)
            {
                rawOptions.ReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel.None;
            }
            else
            {
                rawOptions.ReasoningEffortLevel = default(
                    OpenAI.Chat.ChatReasoningEffortLevel?
                );
            }
#pragma warning restore OPENAI001
        }
    }

    /// <summary>
    /// Streaming counterpart of the usage middleware: forwards updates unchanged while summing any
    /// <see cref="UsageContent" /> into the run's <see cref="LlmUsageSink" /> when one is threaded.
    /// </summary>
    /// <remarks>
    /// Streaming usage typically arrives as a single <see cref="UsageContent" /> on the final
    /// update (Phase 0 spike). A no-op passthrough when no sink is present.
    ///
    /// NOTE: usage is accumulated as each update is pulled from the source. A consumer that
    /// abandons the stream early (cancellation) never pulls the terminal usage update, so an
    /// aborted streaming run may under-count — accepted because (1) no streaming caller threads a
    /// sink today (the review path is non-streaming <c>RunAsync</c>), (2) a cancelled run's token
    /// total is moot, and (3) metrics are loss-tolerant (NFR-4). Accumulating "before yield"
    /// would not help: the missing update is simply never produced when the consumer stops.
    /// </remarks>
    private static async IAsyncEnumerable<ChatResponseUpdate> AccumulateStreamingUsageAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient innerClient,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var sink = LlmUsageSink.Resolve(options);

        await foreach (
            var update in innerClient.GetStreamingResponseAsync(
                messages,
                options,
                cancellationToken
            )
        )
        {
            if (sink is not null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        sink.Add(usageContent.Details);
                    }
                }
            }

            yield return update;
        }
    }

    /// <summary>
    /// Models that reject <c>Temperature = 0</c> and only accept the default value of 1.
    /// Checked case-insensitively against a substring of the configured model identifier.
    /// </summary>
    private static readonly string[] TemperatureZeroUnsupportedModelLabels =
    [
        "gpt-5.5",
        "gpt-5.6",
    ];

    /// <summary>
    /// Models that reject <c>reasoning_effort</c> with function tools on Chat Completions.
    /// Checked case-insensitively against a substring of the configured model identifier.
    /// </summary>
    private static readonly string[] ReasoningWithToolsUnsupportedModelLabels = ["gpt-5.6"];

    /// <summary>
    /// Returns true when <paramref name="model" /> appears in
    /// <see cref="TemperatureZeroUnsupportedModelLabels" />.
    /// </summary>
    private static bool IsTemperatureZeroUnsupportedModel(string model)
    {
        foreach (var label in TemperatureZeroUnsupportedModelLabels)
        {
            if (model.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="model" /> appears in
    /// <see cref="ReasoningWithToolsUnsupportedModelLabels" />.
    /// </summary>
    private static bool IsReasoningWithToolsUnsupportedModel(string model)
    {
        foreach (var label in ReasoningWithToolsUnsupportedModelLabels)
        {
            if (model.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Identifies the Anthropic provider, routed through the native Anthropic SDK.
    /// </summary>
    private static bool IsAnthropicProvider(string provider) =>
        provider.Trim().Equals("Anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the Azure OpenAI provider, routed through the Azure OpenAI SDK.
    /// </summary>
    private static bool IsAzureOpenAiProvider(string provider) =>
        provider.Trim().Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the native OpenAI provider, which needs <c>reasoning_effort=none</c>
    /// rather than an omitted value for GPT-5.6 Chat Completions tool calls.
    /// </summary>
    private static bool IsOpenAiProvider(string provider) =>
        provider.Trim().Equals("OpenAI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies providers currently routed through the OpenAI-compatible SDK path.
    /// </summary>
    /// <remarks>
    /// This is intentionally a narrow allow-list. It prevents a configured
    /// provider string from silently being sent to the wrong SDK shape. The provider
    /// access tester converts the resulting <see cref="NotSupportedException" /> into
    /// a sanitized <c>unsupported_provider</c> response for API callers.
    /// </remarks>
    private static bool IsOpenAiCompatibleProvider(string provider) =>
        provider.Trim().Equals("Fireworks", StringComparison.OrdinalIgnoreCase)
        || provider.Trim().Equals("OpenAI", StringComparison.OrdinalIgnoreCase);
}
