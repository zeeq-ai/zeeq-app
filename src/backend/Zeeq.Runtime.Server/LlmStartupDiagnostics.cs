using Zeeq.Core.Llm;
using Serilog;

namespace Zeeq.Runtime.Server;

/// <summary>
/// Startup-time (pre-DI-build) logging for LLM configuration state. Shared by
/// <c>Program.cs</c> and <see cref="ZeeqWorkerHost"/> so both entry points log the same
/// way, immediately after <c>AddZeeqLlm</c> runs.
/// </summary>
/// <remarks>
/// Uses the static Serilog <see cref="Log"/> logger rather than an injected
/// <c>ILogger&lt;T&gt;</c> because this runs during <c>IServiceCollection</c> configuration,
/// before the DI container is built — the same stage <c>Program.cs</c> already uses
/// <see cref="Log.Information(string, object?[])"/> at for the build-info startup line.
/// <see cref="Zeeq.Core.Llm"/> itself stays logging-framework agnostic (it only depends on
/// <c>Microsoft.Extensions.Logging</c> abstractions, e.g. <see cref="LlmClientFactory"/>'s
/// <c>ILoggerFactory</c>), so this lives in the runtime host instead.
/// </remarks>
internal static class LlmStartupDiagnostics
{
    /// <summary>
    /// Logs the snippet-embedding configuration state so a developer or operator can see at
    /// startup whether embeddings are enabled and, if so, whether a real API key is bound —
    /// rather than only discovering a misconfiguration on the first sweep tick or search call.
    /// </summary>
    public static void LogEmbeddingConfigurationStatus(LlmSettings settings)
    {
        switch (SetupLlm.DescribeEmbeddingConfiguration(settings))
        {
            case LlmEmbeddingConfigurationStatus.Disabled:
                Log.Information(
                    "Snippet embeddings disabled (AppSettings:Llm:Embeddings:Enabled=false) — "
                        + "the sweep will index snippets for full-text search only."
                );
                break;

            case LlmEmbeddingConfigurationStatus.MissingApiKey:
                Log.Warning(
                    "Snippet embeddings are enabled but AppSettings:Llm:Embeddings:ApiKey is not "
                        + "set — the embedding pipeline will fail at the first provider call. Set "
                        + "it via user-secrets locally or Secret Manager in production."
                );
                break;

            case LlmEmbeddingConfigurationStatus.PlaceholderApiKey:
                Log.Warning(
                    "Snippet embeddings are enabled but AppSettings:Llm:Embeddings:ApiKey still "
                        + "holds its appsettings.json placeholder value — the real key was never "
                        + "bound. Set it via user-secrets locally or Secret Manager in production."
                );
                break;

            case LlmEmbeddingConfigurationStatus.Configured:
                Log.Information(
                    "Snippet embeddings configured. Model={Model}; Dimensions={Dimensions}",
                    settings.Embeddings.Model,
                    settings.Embeddings.Dimensions
                );
                break;
        }
    }
}
