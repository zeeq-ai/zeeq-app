using FluentlyHttpClient;
using Microsoft.Extensions.Http;
using Notion.Client;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Default <see cref="IZeeqNotionClientFactory"/> — constructs the SDK's <see cref="NotionClient"/>
/// per credential over the shared, resilience-wrapped <see cref="System.Net.Http.HttpClient"/>.
/// </summary>
internal sealed class ZeeqNotionClientFactory(
    IHttpClientFactory httpClientFactory,
    IHttpMessageHandlerFactory messageHandlers,
    IFluentHttpClientFactory fluentClients
) : IZeeqNotionClientFactory
{
    /// <summary>
    /// The Notion API version this project has been built and tested against — captured in
    /// <c>.agents/scratch/notion/notion.http</c> and confirmed working directly against the live
    /// API. Set explicitly rather than left to the SDK's own default: the installed
    /// <c>Notion.Net</c> 5.0.0 package defaults to the older <c>2025-09-03</c> internally, and the
    /// spec's assumption of a "v6.x" SDK release targeting this version does not exist as a
    /// published package (see <see cref="ZeeqNotionClient"/> remarks for the fuller note).
    /// </summary>
    internal const string ApiVersion = "2026-03-11";

    public IZeeqNotionClient Create(string accessToken)
    {
        // IHttpClientFactory.CreateClient(name) is cheap and intended to be called per use —
        // the underlying HttpMessageHandler (carrying NotionResilience's rate limiter + retry
        // pipeline) is what's actually pooled and reused. This does NOT construct a new
        // handler/socket per call.
        var httpClient = httpClientFactory.CreateClient(NotionResilience.Name);

        var client = NotionClientFactory.Create(
            new ClientOptions
            {
                AuthToken = accessToken,
                HttpClient = httpClient,
                NotionVersion = ApiVersion,
            }
        );

        var fluentClient = fluentClients
            .CreateBuilder($"notion-api-{Guid.NewGuid():N}")
            .WithBaseUrl("https://api.notion.com/v1/")
            .WithHeader("Authorization", $"Bearer {accessToken}")
            .WithHeader("Notion-Version", ApiVersion)
            .WithMessageHandler(
                new PooledHttpMessageHandlerLease(
                    messageHandlers.CreateHandler(NotionResilience.Name)
                )
            )
            .Build(skipAutoRegister: true);

        return new ZeeqNotionClient(client, fluentClient);
    }

    /// <summary>
    /// Lets FluentlyHttpClient use the <see cref="IHttpMessageHandlerFactory"/>-managed handler
    /// chain without disposing that pooled handler when the per-token fluent client is disposed.
    /// </summary>
    private sealed class PooledHttpMessageHandlerLease(HttpMessageHandler inner)
        : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
