using Zeeq.Core.Common;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Zeeq.Core.Llm;

/// <summary>
/// Creates LLM chat clients and agents from resolved runtime credentials.
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// Creates a chat client for a tenant-owned resolved credential.
    /// </summary>
    IChatClient CreateChatClient(ResolvedLlmConfiguration configuration);

    /// <summary>
    /// Creates a chat client for an app-level default model setting.
    /// </summary>
    IChatClient CreateDefaultChatClient(LlmModelDefault configuration);

    /// <summary>
    /// Creates an Agent Framework agent for a tenant-owned resolved credential.
    /// </summary>
    AIAgent CreateAgent(ResolvedLlmConfiguration configuration);

    /// <summary>
    /// Creates a platform-level default embedding generator for snippet indexing/search.
    /// </summary>
    /// <param name="settings">Embedding endpoint/model/key configuration.</param>
    /// <param name="profile">
    /// Selects the retry/timeout posture — <see cref="EmbeddingClientProfile.Batch"/> for the
    /// patient sweep, <see cref="EmbeddingClientProfile.Query"/> for fail-fast interactive search.
    /// </param>
    IEmbeddingGenerator<string, Embedding<float>> CreateDefaultEmbeddingGenerator(
        LlmEmbeddingSettings settings,
        EmbeddingClientProfile profile
    );
}

/// <summary>
/// Selects the retry/timeout posture for a snippet embedding generator instance.
/// </summary>
/// <remarks>
/// Retry is SDK-native (<c>System.ClientModel.ClientRetryPolicy</c>), not a custom middleware
/// layer — see <c>LlmClientFactory.CreateDefaultEmbeddingGenerator</c>. The sweep and the
/// interactive query path want opposite postures, so each gets its own client instance rather
/// than sharing one generator with a call-time-selected policy.
/// </remarks>
public enum EmbeddingClientProfile
{
    /// <summary>
    /// Patient: 5 retries, 30s per-attempt timeout. Used by the snippet indexing sweep, which
    /// already tolerates a full tick's delay.
    /// </summary>
    Batch,

    /// <summary>
    /// Fail-fast: no SDK-level retries, 2s per-attempt timeout. Used by interactive snippet
    /// search, which must degrade to full-text search rather than block on a backoff.
    /// </summary>
    Query,
}

/// <summary>
/// Provider/model/API-key tuple resolved immediately before an LLM call.
/// </summary>
public sealed record ResolvedLlmConfiguration(
    string Provider,
    string Model,
    string ApiKey,
    string KeySource,
    string Endpoint = ""
);
