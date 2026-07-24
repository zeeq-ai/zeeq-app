using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Registers the single shared HTTP resilience pipeline used by every Octokit-backed
/// GitHub client in this project.
/// </summary>
/// <remarks>
/// GitHub is a central dependency for Zeeq's review/comment/check-run flows, so
/// transient failures deserve a real retry policy rather than propagating on the
/// first blip. This is wired once here and reused everywhere a <see cref="Octokit.GitHubClient"/>
/// is constructed (see <see cref="GitHubConnectionFactory"/>) instead of each
/// call site rolling its own retry logic or, worse, having none at all.
/// </remarks>
internal static class GitHubResilience
{
    /// <summary>Name shared by the registered <see cref="System.Net.Http.HttpClient"/> and resilience pipeline.</summary>
    public const string Name = "github-octokit";

    /// <summary>
    /// Caps how long a single retry attempt honors a server-reported <c>Retry-After</c>
    /// delay. GitHub's primary rate-limit reset can be up to an hour away; blocking a
    /// request thread that long defeats the point of a bounded retry policy. Once the
    /// reported delay exceeds this, the strategy falls back to its own bounded
    /// exponential backoff instead of waiting out the full reported window — retries
    /// still exhaust and the caller's own outer retry/redelivery (queue processing,
    /// scheduled resync, etc.) is the correct layer to wait out a long rate-limit window.
    /// </summary>
    internal static readonly TimeSpan MaxHonoredRetryAfter = TimeSpan.FromSeconds(30);

    // NOTE: threads the outbound request's HTTP method into the Polly ResilienceContext
    // (set by GitHubResilienceHandler, read by ShouldRetry below) so retry classification
    // can be idempotency-aware. Microsoft.Extensions.Http.Resilience ships a
    // context.GetRequestMessage() convenience extension for exactly this, but that's a
    // different package than the raw Polly + Polly.Extensions we depend on here — it is
    // not available to us, so this is threaded through manually instead.
    internal static readonly ResiliencePropertyKey<HttpMethod> RequestMethodKey =
        new("zeeq.github.request-method");

    /// <summary>
    /// Registers the named <see cref="System.Net.Http.HttpClient"/> and the resilience
    /// pipeline that its message handler executes.
    /// </summary>
    public static IServiceCollection AddGitHubResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline<string, HttpResponseMessage>(
            Name,
            builder =>
                builder
                    // Retry must be the OUTER strategy and timeout the INNER one — Polly
                    // composes strategies with the first-added as outermost, so
                    // AddRetry(...).AddTimeout(...) gives each attempt its own independent
                    // timeout window. The reverse order (timeout outer) would make the
                    // timeout an overarching budget for the whole retry loop — one slow
                    // attempt could exhaust it and leave no time for any retries at all.
                    // See: https://www.pollydocs.org/pipelines/index.html
                    .AddRetry(
                        new RetryStrategyOptions<HttpResponseMessage>
                        {
                            ShouldHandle = ShouldRetry,
                            MaxRetryAttempts = 4,
                            BackoffType = DelayBackoffType.Exponential,
                            Delay = TimeSpan.FromMilliseconds(500),
                            UseJitter = true,
                            // Honor GitHub's Retry-After when present (secondary rate
                            // limiting, some 5xx/503s); otherwise fall through to the
                            // exponential+jitter backoff configured above.
                            DelayGenerator = static args =>
                                ValueTask.FromResult(
                                    args.Outcome.Result is { } response
                                        ? TryGetRetryAfterDelay(response, out var delay)
                                            ? delay
                                            : (TimeSpan?)null
                                        : null
                                ),
                            // NOTE: dispose a handled-but-discarded response before the next
                            // attempt runs. Raw Polly has no HTTP awareness and won't do this
                            // for us (unlike Microsoft.Extensions.Http.Resilience, which we
                            // deliberately aren't using) — an undisposed 5xx/429/timeout
                            // response can pin its underlying connection until GC finalizes
                            // it, which is a real risk across repeated retry attempts. This
                            // never touches the final response actually returned to the
                            // caller: OnRetry only fires when another attempt follows.
                            OnRetry = static args =>
                            {
                                args.Outcome.Result?.Dispose();
                                return default;
                            },
                        }
                    )
                    // Per-attempt timeout: GitHub's REST API is normally fast; bound each
                    // individual attempt so a hung connection doesn't consume the whole
                    // retry budget on its own.
                    .AddTimeout(TimeSpan.FromSeconds(20))
        );

        services
            .AddHttpClient(Name)
            .AddHttpMessageHandler(sp => new GitHubResilienceHandler(
                sp.GetRequiredService<ResiliencePipelineProvider<string>>()
                    .GetPipeline<HttpResponseMessage>(Name)
            ));

        return services;
    }

    /// <summary>
    /// Determines whether a failed attempt should be retried.
    /// </summary>
    /// <remarks>
    /// NOTE: POST is never retried, regardless of the underlying failure. GitHub's REST
    /// API has no idempotency-key mechanism, so an ambiguous failure (timeout, 5xx) after
    /// a POST cannot be distinguished from "GitHub already created the resource but we
    /// lost the response" — retrying could duplicate a PR comment, check run, or
    /// installation token. GET/HEAD are always safe to retry. PUT/PATCH are treated as
    /// safe too: every PUT/PATCH this project sends is a full-replacement update to an
    /// already-identified resource (e.g. editing a comment body by id), which has no
    /// compounding effect from being applied more than once.
    /// </remarks>
    private static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var retryable =
            args.Outcome.Exception is HttpRequestException or TimeoutRejectedException
            || (args.Outcome.Result is { } response && IsRetryableResponse(response));

        return new ValueTask<bool>(retryable && IsIdempotent(args.Context));
    }

    private static bool IsIdempotent(ResilienceContext context) =>
        context.Properties.GetValue(RequestMethodKey, HttpMethod.Get) != HttpMethod.Post;

    /// <summary>
    /// Determines whether a completed (non-exception) response should be retried.
    /// </summary>
    /// <remarks>
    /// 403 is deliberately narrow: GitHub returns 403 both for genuine permission
    /// failures (an installation missing a scope — retrying is pointless and just
    /// hides the real problem) and for secondary rate limiting (transient — GitHub
    /// documents this as always carrying a <c>Retry-After</c> header). Only the
    /// latter is retried, by checking for that header rather than the status code alone.
    /// </remarks>
    internal static bool IsRetryableResponse(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.RequestTimeout
        || response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.RetryAfter is not null);

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
/// Executes the shared GitHub resilience pipeline around the inner HTTP send.
/// </summary>
/// <remarks>
/// NOTE: this deliberately resends the same <see cref="HttpRequestMessage"/> instance
/// on every retry attempt instead of cloning it — reviewed, and that is safe here.
/// <see cref="HttpClient"/> only rejects reusing a request ("The request message was
/// already sent") when <c>SendAsync</c> is called again as a new <em>top-level</em>
/// call — that check (<c>HttpClient.CheckRequestBeforeSend</c> / <c>MarkAsSent</c>)
/// runs once, at the very start of <see cref="HttpClient.SendAsync(HttpRequestMessage)"/>
/// on the public client, not on each hop through a <see cref="DelegatingHandler"/>
/// chain via <c>base.SendAsync</c>. Since this handler's retries all happen via
/// repeated internal <c>base.SendAsync</c> calls from within a single top-level send
/// (the one Octokit's <c>HttpClientAdapter</c> makes per API call), the mark-as-sent
/// check never re-fires and reuse here never throws — confirmed empirically and by
/// the .NET source: <see href="https://stackoverflow.com/questions/76058694/how-is-polly-able-to-resend-the-same-http-request"/>.
/// </remarks>
internal sealed class GitHubResilienceHandler(ResiliencePipeline<HttpResponseMessage> pipeline)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // A ResilienceContext (rather than a plain CancellationToken) is required here
        // so ShouldRetry can read the request method back out via Properties — see
        // GitHubResilience.RequestMethodKey.
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(GitHubResilience.RequestMethodKey, request.Method);

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
