using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.RateLimiting;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Registers the single shared HTTP resilience pipeline used by every Notion SDK client
/// constructed in this project.
/// </summary>
/// <remarks>
/// Mirrors <c>Zeeq.Integrations.GitHub.GitHubResilience</c> (named <see cref="System.Net.Http.HttpClient"/> +
/// <see cref="ResiliencePipeline{TResult}"/> + <see cref="DelegatingHandler"/>), with one addition:
/// a proactive rate limiter. GitHub's pipeline only reacts to a 403/429 after the fact, which is fine
/// for interactive request volume; a Notion bootstrap crawl instead sits at the documented ~3
/// requests/second ceiling continuously, so throttling must happen before a request is sent, not
/// only after a 429 comes back.
/// </remarks>
internal static class NotionResilience
{
    /// <summary>Name shared by the registered <see cref="System.Net.Http.HttpClient"/> and resilience pipeline.</summary>
    public const string Name = "notion-sdk";

    /// <summary>
    /// Caps how long a single retry attempt honors a server-reported <c>Retry-After</c> delay.
    /// See <c>GitHubResilience.MaxHonoredRetryAfter</c> for the same reasoning: a long reported
    /// window should fall back to the run's own outer redelivery rather than blocking a thread.
    /// </summary>
    internal static readonly TimeSpan MaxHonoredRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers the named <see cref="System.Net.Http.HttpClient"/> and the resilience pipeline
    /// that its message handler executes.
    /// </summary>
    public static IServiceCollection AddNotionResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline<string, HttpResponseMessage>(
            Name,
            builder =>
                builder
                    // Composition order (first-added = outermost, see GitHubResilience's note
                    // on Polly strategy nesting): Retry wraps RateLimiter wraps Timeout, so
                    // EVERY attempt — including retries — acquires its own rate-limit permit
                    // and gets its own timeout window. Putting the limiter outside the retry
                    // loop instead would let retries bypass the throttle entirely, which
                    // defeats the point of a proactive limiter.
                    .AddRetry(
                        new RetryStrategyOptions<HttpResponseMessage>
                        {
                            ShouldHandle = ShouldRetry,
                            MaxRetryAttempts = 4,
                            BackoffType = DelayBackoffType.Exponential,
                            Delay = TimeSpan.FromMilliseconds(500),
                            UseJitter = true,
                            DelayGenerator = static args =>
                                ValueTask.FromResult(
                                    args.Outcome.Result is { } response
                                        ? TryGetRetryAfterDelay(response, out var delay)
                                            ? delay
                                            : (TimeSpan?)null
                                        : null
                                ),
                            OnRetry = static args =>
                            {
                                args.Outcome.Result?.Dispose();
                                return default;
                            },
                        }
                    )
                    // Proactive: keep every outbound request under the documented ~3 rps
                    // average, rather than only reacting to a 429 after the ceiling is
                    // already breached. QueueLimit is unbounded because this pipeline only
                    // serves batch ingest work (bootstrap crawl, dirty-page fetch) — there is
                    // no interactive caller waiting on a response that should fail fast
                    // instead of queueing.
                    // NOTE: code review flagged the unbounded QueueLimit as a memory-pressure
                    // risk under concurrent ingest. Deferred intentionally — this is the
                    // locked spec's explicit design (§5.1: "QueueLimit = int.MaxValue — queue,
                    // never reject, this is batch work"), not an oversight. A per-run page
                    // count already bounds the practical queue depth; revisit only if a real
                    // memory issue is observed in practice.
                    .AddRateLimiter(
                        new SlidingWindowRateLimiter(
                            new SlidingWindowRateLimiterOptions
                            {
                                PermitLimit = 3,
                                Window = TimeSpan.FromSeconds(1),
                                SegmentsPerWindow = 1,
                                QueueLimit = int.MaxValue,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            }
                        )
                    )
                    .AddTimeout(TimeSpan.FromSeconds(20))
        );

        services
            .AddHttpClient(Name)
            .AddHttpMessageHandler(sp => new NotionResilienceHandler(
                sp.GetRequiredService<ResiliencePipelineProvider<string>>()
                    .GetPipeline<HttpResponseMessage>(Name)
            ));

        return services;
    }

    /// <summary>
    /// Determines whether a failed attempt should be retried.
    /// </summary>
    /// <remarks>
    /// Unlike GitHub, every Notion call this project makes is a GET (search, retrieve page,
    /// retrieve page as markdown, retrieve token identity) — there is no POST/PATCH mutation
    /// path yet, so there is no idempotency classification to thread through the pipeline.
    /// </remarks>
    private static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var retryable =
            args.Outcome.Exception
                is HttpRequestException
                    or TimeoutRejectedException
                    or RateLimiterRejectedException
            || (args.Outcome.Result is { } response && IsRetryableResponse(response));

        return new ValueTask<bool>(retryable);
    }

    /// <summary>
    /// Determines whether a completed (non-exception) response should be retried.
    /// </summary>
    internal static bool IsRetryableResponse(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.RequestTimeout
        || response.StatusCode == HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Reads a bounded delay from the response's <c>Retry-After</c> header, if present.
    /// </summary>
    internal static bool TryGetRetryAfterDelay(HttpResponseMessage response, out TimeSpan delay)
    {
        delay = default;
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return false;
        }

        var requested = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow);
        if (requested is null || requested.Value <= TimeSpan.Zero)
        {
            return false;
        }

        delay = requested.Value > MaxHonoredRetryAfter ? MaxHonoredRetryAfter : requested.Value;

        return true;
    }
}

/// <summary>
/// Executes the shared Notion resilience pipeline around the inner HTTP send.
/// </summary>
/// <remarks>
/// See <c>Zeeq.Integrations.GitHub.GitHubResilienceHandler</c>'s note on why resending the same
/// <see cref="HttpRequestMessage"/> instance across retry attempts is safe here — the same
/// single-top-level-send structure applies: the Notion SDK's <see cref="System.Net.Http.HttpClient"/>
/// makes one top-level send per API call, and this handler's retries all happen via internal
/// <c>base.SendAsync</c> calls within that one send.
/// </remarks>
internal sealed class NotionResilienceHandler(ResiliencePipeline<HttpResponseMessage> pipeline)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);

        try
        {
            return await pipeline.ExecuteAsync(
                async ctx => await base.SendAsync(request, ctx.CancellationToken),
                context
            );
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
