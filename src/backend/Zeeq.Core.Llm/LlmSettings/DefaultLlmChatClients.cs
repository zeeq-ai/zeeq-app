using Microsoft.Extensions.AI;

namespace Zeeq.Core.Llm;

/// <summary>
/// App-level default chat clients for Zeeq's Fast, High, and Max LLM tiers.
/// </summary>
/// <remarks>
/// These clients use internal application credentials from configuration. They
/// must not be used for tenant-owned encrypted API keys.
/// </remarks>
public sealed class DefaultLlmChatClients(IChatClient fast, IChatClient high, IChatClient max)
{
    /// <summary>
    /// Fast default chat client.
    /// </summary>
    public IChatClient Fast { get; } = fast;

    /// <summary>
    /// High default chat client.
    /// </summary>
    public IChatClient High { get; } = high;

    /// <summary>
    /// Max default chat client.
    /// </summary>
    public IChatClient Max { get; } = max;
}
