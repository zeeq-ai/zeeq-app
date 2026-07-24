using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// End-to-end tests proving GitHubResilienceHandler actually retries through a real
/// DelegatingHandler chain, resending the same HttpRequestMessage instance per
/// attempt (see the NOTE on GitHubResilienceHandler for why that reuse is safe here).
///
/// dotnet run --project src/backend/Zeeq.Integrations.GitHub.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubResilienceHandlerTests/*"
/// </summary>
public sealed class GitHubResilienceHandlerTests
{
    [Test]
    public async Task SendAsync_PatchWithTransientFailure_RetriesAndResendsSameRequestInstance()
    {
        // PATCH (e.g. editing an existing comment/check-run by id) is idempotent, so it
        // should retry like any other safe method.
        var pipeline = ResolvePipeline();
        var stub = new CountingStubHandler(failFirstAttempts: 1);
        var resilienceHandler = new GitHubResilienceHandler(pipeline) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(resilienceHandler);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://api.github.com/test")
        {
            Content = new StringContent("""{"body":"hello"}"""),
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        // The retry actually ran: two attempts, second one succeeded.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stub.CallCount).IsEqualTo(2);

        // Both attempts saw the exact same instance — no cloning — and it didn't throw.
        await Assert.That(stub.ReceivedRequests[0]).IsSameReferenceAs(stub.ReceivedRequests[1]);
        await Assert.That(stub.ReceivedBodies[0]).IsEqualTo("""{"body":"hello"}""");
        await Assert.That(stub.ReceivedBodies[1]).IsEqualTo("""{"body":"hello"}""");
    }

    [Test]
    public async Task SendAsync_NoRetryNeeded_SendsOriginalBodyOnce()
    {
        var pipeline = ResolvePipeline();
        var stub = new CountingStubHandler(failFirstAttempts: 0);
        var resilienceHandler = new GitHubResilienceHandler(pipeline) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(resilienceHandler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/test");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stub.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task SendAsync_PostWithTransientFailure_DoesNotRetry()
    {
        // POST (e.g. creating a new comment) is never retried — an ambiguous failure
        // could mean GitHub already created the resource, so retrying risks duplicating
        // a PR comment, check run, or installation token.
        var pipeline = ResolvePipeline();
        var stub = new CountingStubHandler(failFirstAttempts: 1);
        var resilienceHandler = new GitHubResilienceHandler(pipeline) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(resilienceHandler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/test")
        {
            Content = new StringContent("""{"body":"hello"}"""),
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(stub.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task SendAsync_RetriedResponse_IsDisposed()
    {
        var pipeline = ResolvePipeline();
        var stub = new CountingStubHandler(failFirstAttempts: 1);
        var resilienceHandler = new GitHubResilienceHandler(pipeline) { InnerHandler = stub };
        using var invoker = new HttpMessageInvoker(resilienceHandler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/test");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // The discarded (retried) response's disposal is observable: reading its
        // content after the fact throws ObjectDisposedException.
        await Assert.That(stub.DiscardedResponses.Count).IsEqualTo(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await stub.DiscardedResponses[0].Content.ReadAsStringAsync()
        );
    }

    private static ResiliencePipeline<HttpResponseMessage> ResolvePipeline()
    {
        var services = new ServiceCollection();
        services.AddGitHubResilience();
        var provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline<HttpResponseMessage>(GitHubResilience.Name);
    }

    private sealed class CountingStubHandler(int failFirstAttempts) : DelegatingHandler
    {
        public int CallCount { get; private set; }
        public List<HttpRequestMessage> ReceivedRequests { get; } = [];
        public List<string> ReceivedBodies { get; } = [];

        /// <summary>Responses this stub returned that were classified as retryable (i.e. expected to be disposed by OnRetry).</summary>
        public List<HttpResponseMessage> DiscardedResponses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            ReceivedRequests.Add(request);
            ReceivedBodies.Add(
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)
            );

            if (CallCount <= failFirstAttempts)
            {
                // Real content (not the default empty content) so disposal is actually
                // observable: reading a disposed default-content HttpResponseMessage
                // silently returns an empty string rather than throwing.
                var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("service unavailable"),
                };
                DiscardedResponses.Add(failure);
                return failure;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
