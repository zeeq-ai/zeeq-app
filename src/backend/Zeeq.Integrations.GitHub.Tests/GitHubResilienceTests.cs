using System.Net;
using System.Net.Http.Headers;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// Tests for the shared GitHub HTTP resilience decision logic.
///
/// dotnet run --project src/backend/Zeeq.Integrations.GitHub.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubResilienceTests/*"
/// </summary>
public sealed class GitHubResilienceTests
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

        await Assert.That(GitHubResilience.IsRetryableResponse(response)).IsTrue();
    }

    [Test]
    public async Task IsRetryableResponse_Forbidden_WithoutRetryAfter_ReturnsFalse()
    {
        // A permission failure (e.g. installation missing a scope) — retrying
        // this forever would just hide a real, non-transient problem.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Assert.That(GitHubResilience.IsRetryableResponse(response)).IsFalse();
    }

    [Test]
    public async Task IsRetryableResponse_Forbidden_WithRetryAfter_ReturnsTrue()
    {
        // GitHub's secondary rate limit: 403 with a Retry-After header.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));

        await Assert.That(GitHubResilience.IsRetryableResponse(response)).IsTrue();
    }

    [Test]
    public async Task IsRetryableResponse_Success_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        await Assert.That(GitHubResilience.IsRetryableResponse(response)).IsFalse();
    }

    [Test]
    public async Task IsRetryableResponse_NotFound_ReturnsFalse()
    {
        // A 404 is a real answer, not a transient failure — never retry it.
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);

        await Assert.That(GitHubResilience.IsRetryableResponse(response)).IsFalse();
    }

    [Test]
    public async Task TryGetRetryAfterDelay_NoHeader_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var found = GitHubResilience.TryGetRetryAfterDelay(response, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TryGetRetryAfterDelay_DeltaSecondsWithinCap_ReturnsRequestedDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));

        var found = GitHubResilience.TryGetRetryAfterDelay(response, out var delay);

        await Assert.That(found).IsTrue();
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TryGetRetryAfterDelay_ExceedsCap_ReturnsCappedDelay()
    {
        // GitHub's primary rate-limit reset can be up to an hour away; the
        // pipeline must not block a request thread that long.
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));

        var found = GitHubResilience.TryGetRetryAfterDelay(response, out var delay);

        await Assert.That(found).IsTrue();
        await Assert.That(delay).IsEqualTo(GitHubResilience.MaxHonoredRetryAfter);
    }

    [Test]
    public async Task TryGetRetryAfterDelay_ZeroOrNegative_ReturnsFalse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);

        var found = GitHubResilience.TryGetRetryAfterDelay(response, out _);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task TryGetRetryAfterDelay_HttpDateForm_ReturnsPositiveDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddSeconds(15)
        );

        var found = GitHubResilience.TryGetRetryAfterDelay(response, out var delay);

        await Assert.That(found).IsTrue();
        await Assert.That(delay).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(delay).IsLessThanOrEqualTo(TimeSpan.FromSeconds(15));
    }
}
