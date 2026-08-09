using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;

namespace Zeeq.Integrations.Notion.Tests;

/// <summary>
/// Tests for the shared Notion HTTP resilience decision logic and end-to-end retry behavior.
///
/// dotnet run --project src/backend/Zeeq.Integrations.Notion.Tests --output detailed --disable-logo --treenode-filter "/*/*/NotionResilienceTests/*"
/// </summary>
public sealed class NotionResilienceTests
{
    [Test]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.BadGateway)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.RequestTimeout)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    public async Task IsRetryableResponse_TransientStatusCodes_ReturnsTrue(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);

        await Assert.That(NotionResilience.IsRetryableResponse(response)).IsTrue();
    }

    [Test]
    public async Task IsRetryableResponse_Success_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        await Assert.That(NotionResilience.IsRetryableResponse(response)).IsFalse();
    }

    [Test]
    public async Task IsRetryableResponse_NotFound_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);

        await Assert.That(NotionResilience.IsRetryableResponse(response)).IsFalse();
    }

    [Test]
    public async Task TryGetRetryAfterDelay_DeltaSecondsWithinCap_ReturnsRequestedDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));

        var found = NotionResilience.TryGetRetryAfterDelay(response, out var delay);

        await Assert.That(found).IsTrue();
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TryGetRetryAfterDelay_ExceedsCap_ReturnsCappedDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));

        var found = NotionResilience.TryGetRetryAfterDelay(response, out var delay);

        await Assert.That(found).IsTrue();
        await Assert.That(delay).IsEqualTo(NotionResilience.MaxHonoredRetryAfter);
    }

    [Test]
    public async Task NotionClient_RateLimited429_HonorsRetryAfter()
    {
        // 429 with a short Retry-After should be retried and eventually succeed, rather
        // than surfacing the 429 to the caller.
        var pipeline = ResolvePipeline();
        var stub = new CountingStubHandler(
            failFirstAttempts: 1,
            retryAfter: TimeSpan.FromMilliseconds(50)
        );
        var resilienceHandler = new NotionResilienceHandler(pipeline) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(resilienceHandler);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.notion.com/v1/search"
        )
        {
            Content = new StringContent("""{"query":""}"""),
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stub.CallCount).IsEqualTo(2);
    }

    private static ResiliencePipeline<HttpResponseMessage> ResolvePipeline()
    {
        var services = new ServiceCollection();
        services.AddNotionResilience();
        var provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline<HttpResponseMessage>(NotionResilience.Name);
    }

    private sealed class CountingStubHandler(int failFirstAttempts, TimeSpan retryAfter)
        : DelegatingHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;

            if (CallCount <= failFirstAttempts)
            {
                var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                failure.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                return Task.FromResult(failure);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
