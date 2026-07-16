using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Zeeq.Integrations.GitHub;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class PublicRepositoryVisibilityCheckerTests
{
    private static PublicRepositoryVisibilityChecker Checker(
        IGitHubRepositoryVisibilityClient client
    ) => new(client, NullLogger<PublicRepositoryVisibilityChecker>.Instance);

    [Test]
    public async Task CheckAsync_RepositoryNotPrivate_ReturnsPublic()
    {
        var client = new FakeRepositoryVisibilityClient { IsPrivate = false };
        var checker = Checker(client);

        var result = await checker.CheckAsync(
            "https://github.com/octocat/Hello-World",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.Public);
        await Assert.That(client.LastOwner).IsEqualTo("octocat");
        await Assert.That(client.LastName).IsEqualTo("Hello-World");
    }

    [Test]
    public async Task CheckAsync_RepositoryPrivate_ReturnsNotPubliclyAccessible()
    {
        var client = new FakeRepositoryVisibilityClient { IsPrivate = true };
        var checker = Checker(client);

        var result = await checker.CheckAsync(
            "https://github.com/acme/internal-repo",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.NotPubliclyAccessible);
    }

    [Test]
    public async Task CheckAsync_NotFoundException_ReturnsNotPubliclyAccessible()
    {
        // GitHub returns 404 for both "went private" and "deleted" when
        // checked anonymously — indistinguishable, both treated the same.
        var client = new FakeRepositoryVisibilityClient
        {
            ExceptionToThrow = new NotFoundException(
                "not found",
                System.Net.HttpStatusCode.NotFound
            ),
        };
        var checker = Checker(client);

        var result = await checker.CheckAsync(
            "https://github.com/acme/gone",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.NotPubliclyAccessible);
    }

    [Test]
    public async Task CheckAsync_OtherException_ReturnsTransientError()
    {
        var client = new FakeRepositoryVisibilityClient
        {
            ExceptionToThrow = new HttpRequestException("network blip"),
        };
        var checker = Checker(client);

        var result = await checker.CheckAsync(
            "https://github.com/acme/repo",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.TransientError);
    }

    [Test]
    public async Task CheckAsync_UnparseableUrl_ReturnsTransientErrorWithoutCallingClient()
    {
        var client = new FakeRepositoryVisibilityClient { IsPrivate = false };
        var checker = Checker(client);

        var result = await checker.CheckAsync("not-a-url", CancellationToken.None);

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.TransientError);
        await Assert.That(client.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_UrlWithGitSuffix_ParsesOwnerAndName()
    {
        var client = new FakeRepositoryVisibilityClient { IsPrivate = false };
        var checker = Checker(client);

        var result = await checker.CheckAsync(
            "https://github.com/zeeq-ai/zeeq.git",
            CancellationToken.None
        );

        await Assert.That(result).IsEqualTo(RepositoryVisibilityCheckResult.Public);
        await Assert.That(client.LastOwner).IsEqualTo("zeeq-ai");
        await Assert.That(client.LastName).IsEqualTo("zeeq");
    }

    private sealed class FakeRepositoryVisibilityClient : IGitHubRepositoryVisibilityClient
    {
        public bool IsPrivate { get; init; }
        public Exception? ExceptionToThrow { get; init; }
        public int CallCount { get; private set; }
        public string? LastOwner { get; private set; }
        public string? LastName { get; private set; }

        public Task<bool> IsPrivateAsync(
            string owner,
            string name,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            LastOwner = owner;
            LastName = name;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(IsPrivate);
        }
    }
}
