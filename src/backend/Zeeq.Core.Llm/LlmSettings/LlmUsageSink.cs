using Microsoft.Extensions.AI;

namespace Zeeq.Core.Llm;

/// <summary>
/// Per-run accumulator for LLM token usage across every round trip of a single agent run.
/// </summary>
/// <remarks>
/// <para>
/// Threaded to a run through <c>ChatOptions.AdditionalProperties[<see cref="RunOptionsKey" />]</c>.
/// A usage-observing middleware sits <b>below</b> function invocation in
/// <see cref="LlmClientFactory" />'s chat-client chain and calls <see cref="Add" /> on every
/// <c>ChatResponse</c> — so this sums the true total across the tool-calling loop.
/// </para>
/// <para>
/// This exists because neither <c>AgentRunResponse.Usage</c> nor the post-function-invocation
/// <c>ChatResponse.Usage</c> aggregates across round trips (verified across Fireworks, Azure
/// OpenAI, OpenAI, and Anthropic in the Phase 0 spike): both report only the first round trip, so
/// relying on them would systematically under-count reviews that call KB tools. Accumulating each
/// round trip here is the only path to an accurate per-run total.
/// </para>
/// <para>
/// Round trips within a run are sequential, but <see cref="Interlocked" /> keeps this safe even if
/// a client ever fans requests out concurrently on one options instance.
/// </para>
/// </remarks>
public sealed class LlmUsageSink
{
    /// <summary>Key under which a sink is threaded through <c>ChatOptions.AdditionalProperties</c>.</summary>
    /// <remarks>
    /// NOTE: this is a stringly-typed contract on <c>AdditionalProperties</c>. Every read and
    /// write MUST funnel through <see cref="Resolve" /> / <see cref="AttachTo" /> so the key never
    /// leaks into new call sites and the sink shape cannot silently drift.
    /// </remarks>
    public const string RunOptionsKey = "zeeq.llm.usage_sink";

    private long _inputTokens;
    private long _outputTokens;
    private long _totalTokens;

    /// <summary>Accumulated input tokens across the run.</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);

    /// <summary>Accumulated output tokens across the run.</summary>
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>Accumulated total tokens across the run.</summary>
    public long TotalTokens => Interlocked.Read(ref _totalTokens);

    /// <summary>
    /// Whether any usage was recorded. False when a provider populated no usage — the caller then
    /// emits no token metric rather than a misleading zero.
    /// </summary>
    public bool HasUsage => Interlocked.Read(ref _totalTokens) > 0;

    /// <summary>Adds one round trip's usage; a null or empty <paramref name="usage" /> is ignored.</summary>
    public void Add(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
        Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
        Interlocked.Add(ref _totalTokens, usage.TotalTokenCount ?? 0);
    }

    /// <summary>
    /// Resolves the sink threaded onto <paramref name="options" />, or null when none is present.
    /// </summary>
    /// <remarks>
    /// The single, typed read side of the <see cref="RunOptionsKey" /> contract — the usage
    /// middleware calls this so no caller hand-rolls the dictionary lookup or type check. Returns
    /// null (a no-op) for every call that did not thread a sink, which is every non-review caller.
    /// </remarks>
    public static LlmUsageSink? Resolve(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties
        && properties.TryGetValue(RunOptionsKey, out var raw)
        && raw is LlmUsageSink sink
            ? sink
            : null;

    /// <summary>Threads this sink onto <paramref name="options" /> for the usage middleware to find.</summary>
    public void AttachTo(ChatOptions options) =>
        (options.AdditionalProperties ??= [])[RunOptionsKey] = this;
}
