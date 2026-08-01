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
    private long _cachedInputTokens;
    private long _outputTokens;
    private long _totalTokens;
    private int _hasInputTokens;
    private int _hasCachedInputTokens;
    private int _hasOutputTokens;
    private int _hasTotalTokens;

    /// <summary>Accumulated input tokens across the run.</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);

    /// <summary>Accumulated cached input tokens across the run.</summary>
    public long CachedInputTokens => Interlocked.Read(ref _cachedInputTokens);

    /// <summary>Accumulated output tokens across the run.</summary>
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>Accumulated total tokens across the run.</summary>
    public long TotalTokens => Interlocked.Read(ref _totalTokens);

    /// <summary>Accumulated input tokens, or null when the provider never reported the field.</summary>
    public long? InputTokensOrNull => HasInputTokens ? InputTokens : null;

    /// <summary>Accumulated cached input tokens, or null when the provider never reported the field.</summary>
    public long? CachedInputTokensOrNull => HasCachedInputTokens ? CachedInputTokens : null;

    /// <summary>Accumulated output tokens, or null when the provider never reported the field.</summary>
    public long? OutputTokensOrNull => HasOutputTokens ? OutputTokens : null;

    /// <summary>Accumulated total tokens, or null when the provider never reported the field.</summary>
    public long? TotalTokensOrNull => HasTotalTokens ? TotalTokens : null;

    /// <summary>Whether input tokens were reported at least once.</summary>
    public bool HasInputTokens => Interlocked.CompareExchange(ref _hasInputTokens, 0, 0) != 0;

    /// <summary>Whether cached input tokens were reported at least once.</summary>
    public bool HasCachedInputTokens =>
        Interlocked.CompareExchange(ref _hasCachedInputTokens, 0, 0) != 0;

    /// <summary>Whether output tokens were reported at least once.</summary>
    public bool HasOutputTokens => Interlocked.CompareExchange(ref _hasOutputTokens, 0, 0) != 0;

    /// <summary>Whether total tokens were reported at least once.</summary>
    public bool HasTotalTokens => Interlocked.CompareExchange(ref _hasTotalTokens, 0, 0) != 0;

    /// <summary>
    /// Whether any usage was recorded. False when a provider populated no usage — the caller then
    /// emits no token metric rather than a misleading zero.
    /// </summary>
    public bool HasUsage =>
        HasInputTokens || HasCachedInputTokens || HasOutputTokens || HasTotalTokens;

    /// <summary>Adds one round trip's usage; a null or empty <paramref name="usage" /> is ignored.</summary>
    public void Add(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        AddIfReported(ref _inputTokens, ref _hasInputTokens, usage.InputTokenCount);
        AddIfReported(
            ref _cachedInputTokens,
            ref _hasCachedInputTokens,
            usage.CachedInputTokenCount
        );
        AddIfReported(ref _outputTokens, ref _hasOutputTokens, usage.OutputTokenCount);
        AddIfReported(ref _totalTokens, ref _hasTotalTokens, usage.TotalTokenCount);
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

    private static void AddIfReported(ref long total, ref int hasValue, long? value)
    {
        if (value is null)
        {
            return;
        }

        Interlocked.Exchange(ref hasValue, 1);
        Interlocked.Add(ref total, value.Value);
    }
}
