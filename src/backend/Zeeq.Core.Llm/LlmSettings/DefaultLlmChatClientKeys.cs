namespace Zeeq.Core.Llm;

/// <summary>
/// Stable keyed-service names for app-level default LLM chat clients.
/// </summary>
public static class DefaultLlmChatClientKeys
{
    /// <summary>
    /// Fast tier keyed service name.
    /// </summary>
    public const string Fast = "llm.default.fast";

    /// <summary>
    /// High tier keyed service name.
    /// </summary>
    public const string High = "llm.default.high";

    /// <summary>
    /// Max tier keyed service name.
    /// </summary>
    public const string Max = "llm.default.max";

    /// <summary>
    /// Batch-profile snippet embedding generator: patient retries, used by the snippet
    /// indexing sweep (a tick already tolerates delay).
    /// </summary>
    public const string SnippetEmbeddingsBatch = "llm.default.embeddings.batch";

    /// <summary>
    /// Query-profile snippet embedding generator: near-zero retry budget, used by
    /// interactive snippet search (fails fast and degrades to full-text search rather
    /// than making a caller wait out a backoff).
    /// </summary>
    public const string SnippetEmbeddingsQuery = "llm.default.embeddings.query";
}
