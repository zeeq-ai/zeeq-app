namespace Zeeq.Integrations.Notion;

/// <summary>
/// Creates <see cref="IZeeqNotionClient"/> instances authenticated with a caller-supplied
/// Notion access token.
/// </summary>
/// <remarks>
/// Direct analogue of <c>Zeeq.Integrations.GitHub.IGitHubClientFactory</c> /
/// <c>GitHubConnectionFactory</c>: one client per credential, over the single shared,
/// resilience-wrapped <see cref="System.Net.Http.HttpClient"/> registered by
/// <see cref="NotionResilience"/>.
///
/// The SDK's own DI helper, <c>services.AddNotionClient(options => { options.AuthToken = "&lt;Token&gt;"; })</c>,
/// binds ONE static token at registration time. Zeeq resolves a different token per library at
/// runtime (from <c>EncryptedValue</c>), so that helper cannot be used — this factory exists
/// specifically to construct a per-credential client on demand instead.
/// </remarks>
public interface IZeeqNotionClientFactory
{
    /// <summary>
    /// Creates a client authenticated with the given pasted Notion access token.
    /// </summary>
    /// <remarks>
    /// Does NOT construct a new <see cref="System.Net.Http.HttpClient"/> per call — that would
    /// defeat <see cref="IHttpClientFactory"/> pooling and re-instate the socket-exhaustion
    /// problem <see cref="NotionResilience"/>'s named client exists to avoid. Every client this
    /// factory returns shares the one pooled, rate-limited, retry-wrapped transport.
    /// </remarks>
    IZeeqNotionClient Create(string accessToken);
}
