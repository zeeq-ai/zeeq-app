using Octokit;
using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// Tests for the GitHub pull request source adapter.
///
/// dotnet run --project src/backend/Zeeq.Integrations.GitHub.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubCodeReviewPullRequestSourceTests/*"
/// </summary>
public sealed class GitHubCodeReviewPullRequestSourceTests
{
    [Test]
    public async Task GetPullRequestAsync_UsesInstallationClientAndMapsPullRequestFiles()
    {
        var dataClient = new FakePullRequestDataClient(
            new(
                "Add source",
                "Fetch GitHub source data.",
                [
                    new("src/added.cs", null, "added", "+added"),
                    new("src/modified.cs", null, "modified", "+modified"),
                    new("src/deleted.cs", null, "removed", "-deleted"),
                    new("src/new-name.cs", "src/old-name.cs", "renamed", "+renamed"),
                    new("src/copied.cs", "src/source.cs", "copied", "+copied"),
                    new("assets/logo.png", null, "binary", string.Empty),
                ],
                [],
                []
            )
        );
        var factory = new FakeGitHubClientFactory();
        var source = CreateSource(factory, dataClient);

        var snapshot = await source.GetPullRequestAsync(Message(), CancellationToken.None);

        await Assert.That(factory.OrganizationIds).IsEquivalentTo(["org_123"]);
        await Assert.That(dataClient.Owner).IsEqualTo("zeeq-ai");
        await Assert.That(dataClient.RepositoryName).IsEqualTo("zeeq");
        await Assert.That(dataClient.PullRequestNumber).IsEqualTo(42);
        await Assert.That(snapshot.Title).IsEqualTo("Add source");
        await Assert.That(snapshot.Body).IsEqualTo("Fetch GitHub source data.");
        await Assert
            .That(snapshot.Files.Select(file => (file.Path, file.PreviousPath, file.MutationState)))
            .IsEquivalentTo([
                ("src/added.cs", null, CodeReviewFileMutationState.Added),
                ("src/modified.cs", null, CodeReviewFileMutationState.Modified),
                ("src/deleted.cs", null, CodeReviewFileMutationState.Deleted),
                ("src/new-name.cs", "src/old-name.cs", CodeReviewFileMutationState.Renamed),
                ("src/copied.cs", "src/source.cs", CodeReviewFileMutationState.Copied),
                ("assets/logo.png", null, CodeReviewFileMutationState.Binary),
            ]);
    }

    [Test]
    public async Task GetPullRequestAsync_WithDeveloperFeedback_FiltersNoiseAndCapsLatest()
    {
        var issueComments = Enumerable
            .Range(0, 25)
            .Select(index => new GitHubIssueCommentData(
                $"human-{index:00}",
                $"human issue feedback {index:00}",
                DateTimeOffset.Parse("2026-06-25T12:00:00Z").AddMinutes(index),
                $"https://github.example/issues/{index}"
            ))
            .Concat([
                new(
                    "zeeq-code-review-agent",
                    "Zeeq generated status.",
                    DateTimeOffset.Parse("2026-06-25T13:00:00Z"),
                    null
                ),
                new(
                    "octocat",
                    "/zeeq please review",
                    DateTimeOffset.Parse("2026-06-25T13:01:00Z"),
                    null
                ),
                new(
                    "github-actions[bot]",
                    "Automated noise.",
                    DateTimeOffset.Parse("2026-06-25T13:02:00Z"),
                    null
                ),
            ])
            .ToArray();

        var dataClient = new FakePullRequestDataClient(
            new(
                "Add source",
                "Fetch GitHub source data.",
                [],
                issueComments,
                [
                    new(
                        "reviewer",
                        "inline human feedback",
                        DateTimeOffset.Parse("2026-06-25T13:03:00Z"),
                        "https://github.example/review-comment/1",
                        "src/file.cs",
                        12
                    ),
                    new(
                        "dependabot[bot]",
                        "inline bot feedback",
                        DateTimeOffset.Parse("2026-06-25T13:04:00Z"),
                        "https://github.example/review-comment/2",
                        "src/file.cs",
                        14
                    ),
                ]
            )
        );
        var source = CreateSource(new FakeGitHubClientFactory(), dataClient);

        var snapshot = await source.GetPullRequestAsync(Message(), CancellationToken.None);

        await Assert.That(snapshot.DeveloperFeedbackComments).Count().IsEqualTo(20);
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .Contains("inline human feedback");
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .DoesNotContain("Zeeq generated status.");
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .DoesNotContain("/zeeq please review");
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .DoesNotContain("Automated noise.");
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .DoesNotContain("inline bot feedback");
        await Assert
            .That(snapshot.DeveloperFeedbackComments.Select(comment => comment.Body))
            .DoesNotContain("human issue feedback 00");
    }

    [Test]
    public async Task GetPullRequestAsync_WhenInstallationIsMissing_ThrowsBeforeFetching()
    {
        var dataClient = new FakePullRequestDataClient(
            new("Add source", "Fetch GitHub source data.", [], [], [])
        );
        var source = CreateSource(
            new FakeGitHubClientFactory(throwMissingInstallation: true),
            dataClient
        );

        await Assert
            .That(async () => await source.GetPullRequestAsync(Message(), CancellationToken.None))
            .Throws<GitHubInstallationUnavailableException>();
        await Assert.That(dataClient.WasCalled).IsFalse();
    }

    private static GitHubCodeReviewPullRequestSource CreateSource(
        IGitHubClientFactory factory,
        IGitHubPullRequestDataClient dataClient
    ) =>
        new(
            factory,
            dataClient,
            new CodeReviewSettings { AgentIdentity = "zeeq-code-review-agent" }
        );

    private static CodeReviewRunRequested Message() =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            PullRequestRecordId = "pr_123",
            PullRequestCreatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            CodeReviewRecordId = "cr_123",
            CodeReviewCreatedAtUtc = DateTimeOffset.Parse("2026-06-25T12:00:00Z"),
            GitHubDeliveryId = "delivery_123",
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private sealed class FakeGitHubClientFactory(bool throwMissingInstallation = false)
        : IGitHubClientFactory
    {
        public List<string> OrganizationIds { get; } = [];

        public Task<GitHubClient> CreateInstallationClientForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        )
        {
            OrganizationIds.Add(organizationId);
            if (throwMissingInstallation)
            {
                throw new GitHubInstallationUnavailableException(organizationId);
            }

            return Task.FromResult(new GitHubClient(new ProductHeaderValue("zeeq-test")));
        }
    }

    private sealed class FakePullRequestDataClient(GitHubPullRequestSourceData data)
        : IGitHubPullRequestDataClient
    {
        public string? Owner { get; private set; }
        public string? RepositoryName { get; private set; }
        public int PullRequestNumber { get; private set; }
        public bool WasCalled { get; private set; }

        public Task<GitHubPullRequestSourceData> GetAsync(
            GitHubClient client,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken
        )
        {
            WasCalled = true;
            Owner = owner;
            RepositoryName = repositoryName;
            PullRequestNumber = pullRequestNumber;

            return Task.FromResult(data);
        }
    }
}
