using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit.Webhooks;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using Octokit.Webhooks.Models;
using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Messaging;
using GitHubUser = Octokit.Webhooks.Models.User;
using PullRequestBase = Octokit.Webhooks.Models.PullRequestEvent.PullRequestBase;
using PullRequestHead = Octokit.Webhooks.Models.PullRequestEvent.PullRequestHead;
using PullRequestPayload = Octokit.Webhooks.Models.PullRequestEvent.PullRequest;
using ReviewCommentPayload = Octokit.Webhooks.Models.PullRequestReviewComment;
using SimplePullRequestPayload = Octokit.Webhooks.Models.SimplePullRequest;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubWebhookEndpointMappingTests
{
    [Test]
    public async Task MapZeeqGitHubWebhooks_MapsExpectedPathWithRequestSizeLimit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddHybridCache();
        builder.Services.AddSingleton<ICodeRepositoryStore>(
            new FakeCodeRepositoryStore(CreateRepository())
        );
        builder.Services.AddSingleton<IZeeqMessagePublisher>(new CapturingPublisher());
        builder.Services.AddScoped<GitHubWebhookRepositoryGate>();
        builder.Services.AddScoped<WebhookEventProcessor, ZeeqGitHubWebhookEventProcessor>();

        await using var app = builder.Build();

        app.MapZeeqGitHubWebhooks(new GitHubSettings { WebhookSecret = "secret" });

        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.RoutePattern.RawText == GitHubWebhookEndpointMapping.WebhookPath
            );
        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        await Assert.That(limit).IsNotNull();
        await Assert
            .That(limit!.MaxRequestBodySize)
            .IsEqualTo(GitHubWebhookEndpointMapping.MaxRequestBodyBytes);
    }

    [Test]
    public async Task Processor_WithMappedPullRequest_PublishesTenantMessage()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessPullRequestAsync();

        var published = publisher.Published.Single();
        await Assert.That(published).IsTypeOf<GitHubPullRequestWebhookReceived>();

        var message = (GitHubPullRequestWebhookReceived)published;
        await Assert.That(message.OrganizationId).IsEqualTo("org_1");
        await Assert.That(message.RepositoryId).IsEqualTo("repo_1");
        await Assert.That(message.OwnerQualifiedRepoName).IsEqualTo("zeeq-ai/zeeq");
        await Assert.That(message.GitHubDeliveryId).IsEqualTo("delivery-1");
        await Assert.That(message.PullRequestNumber).IsEqualTo(42);
        await Assert.That(message.HeadSha).IsEqualTo("abc123");
    }

    [Test]
    public async Task Processor_WithMissingRepositoryMapping_DoesNotPublish()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(logger, new FakeCodeRepositoryStore(null), publisher);

        await processor.ProcessPullRequestAsync();

        await Assert.That(publisher.Published).IsEmpty();
        await Assert.That(logger.Messages.Last()).Contains("has no configured repository mapping");
    }

    [Test]
    public async Task Processor_WithPullRequestIssueComment_PublishesTenantMessage()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessIssueCommentAsync(isPullRequest: true);

        var published = publisher.Published.Single();
        await Assert.That(published).IsTypeOf<GitHubIssueCommentWebhookReceived>();

        var message = (GitHubIssueCommentWebhookReceived)published;
        await Assert.That(message.PullRequestNumber).IsEqualTo(42);
        await Assert.That(message.CommentId).IsEqualTo(9001);
        await Assert.That(message.CommentAuthorLogin).IsEqualTo("octo-user");
    }

    [Test]
    public async Task Processor_WithPlainIssueComment_DoesNotPublish()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessIssueCommentAsync(isPullRequest: false);

        await Assert.That(publisher.Published).IsEmpty();
        await Assert.That(logger.Messages.Last()).Contains("was filtered before queue publish");
    }

    [Test]
    public async Task Processor_WithPullRequestIssueCommentWithoutCommand_DoesNotPublish()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessIssueCommentAsync(
            isPullRequest: true,
            commentBody: "This is normal PR discussion."
        );

        await Assert.That(publisher.Published).IsEmpty();
        await Assert.That(logger.Messages.Last()).Contains("was filtered before queue publish");
    }

    [Test]
    public async Task Processor_WithPullRequestReviewComment_PublishesTenantMessage()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessReviewCommentAsync();

        var published = publisher.Published.Single();
        await Assert.That(published).IsTypeOf<GitHubPullRequestReviewCommentWebhookReceived>();

        var message = (GitHubPullRequestReviewCommentWebhookReceived)published;
        await Assert.That(message.PullRequestNumber).IsEqualTo(42);
        await Assert.That(message.CommentId).IsEqualTo(7001);
        await Assert.That(message.Path).IsEqualTo("src/app.cs");
        await Assert.That(message.CommitId).IsEqualTo("def456");
    }

    [Test]
    public async Task Processor_WithPullRequestReviewCommentWithoutCommand_DoesNotPublish()
    {
        var logger = new CapturingLogger<ZeeqGitHubWebhookEventProcessor>();
        var publisher = new CapturingPublisher();
        var processor = CreateProcessor(
            logger,
            new FakeCodeRepositoryStore(CreateRepository()),
            publisher
        );

        await processor.ProcessReviewCommentAsync(commentBody: "Nit: rename this local.");

        await Assert.That(publisher.Published).IsEmpty();
        await Assert.That(logger.Messages.Last()).Contains("was filtered before queue publish");
    }

    private static TestableGitHubWebhookEventProcessor CreateProcessor(
        ILogger<ZeeqGitHubWebhookEventProcessor> logger,
        ICodeRepositoryStore store,
        IZeeqMessagePublisher publisher
    )
    {
        var services = new ServiceCollection();
        services.AddHybridCache();

        var serviceProvider = services.BuildServiceProvider();
        var gate = new GitHubWebhookRepositoryGate(
            serviceProvider.GetRequiredService<HybridCache>(),
            store,
            NullLogger<GitHubWebhookRepositoryGate>.Instance
        );

        return new(
            logger,
            gate,
            new NoOpGitHubInstallationStore(),
            publisher,
            new NoOpPullRequestRecordStore()
        );
    }

    private static CodeRepository CreateRepository() =>
        new()
        {
            Id = "repo_1",
            OrganizationId = "org_1",
            TeamId = "team_1",
            Provider = "github",
            OwnerQualifiedName = "zeeq-ai/zeeq",
            DisplayName = "zeeq-ai/zeeq",
            Enabled = true,
        };

    private sealed class NoOpPullRequestRecordStore : IPullRequestRecordStore
    {
        public Task<PullRequestRecord> UpsertAsync(
            PullRequestRecord pullRequest,
            CancellationToken cancellationToken
        ) => Task.FromResult(pullRequest);

        public Task<PullRequestRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) => Task.FromResult<PullRequestRecord?>(null);

        public Task<PullRequestRecord?> FindByHeadShaAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => Task.FromResult<PullRequestRecord?>(null);

        public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => Task.FromResult<PullRequestRecord?>(null);

        public Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
            string organizationId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<PullRequestRecord>>([]);

        public Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
            PullRequestStreamQuery query,
            CancellationToken cancellationToken
        ) => Task.FromResult(new CodeReviewStreamPage<PullRequestRecord>([], null, null));
    }

    private sealed class TestableGitHubWebhookEventProcessor : ZeeqGitHubWebhookEventProcessor
    {
        public TestableGitHubWebhookEventProcessor(
            ILogger<ZeeqGitHubWebhookEventProcessor> logger,
            GitHubWebhookRepositoryGate repositoryGate,
            IGitHubInstallationStore installationStore,
            IZeeqMessagePublisher publisher,
            IPullRequestRecordStore pullRequestStore
        )
            : base(logger, repositoryGate, installationStore, publisher, pullRequestStore) { }

        public ValueTask ProcessPullRequestAsync(string deliveryId = "delivery-1") =>
            base.ProcessPullRequestWebhookAsync(
                new WebhookHeaders { Delivery = deliveryId, Event = "pull_request" },
                Model<Octokit.Webhooks.Events.PullRequest.PullRequestOpenedEvent>(payload =>
                {
                    Set(
                        payload,
                        nameof(payload.Installation),
                        Model<InstallationLite>(installation =>
                        {
                            Set(installation, nameof(installation.Id), 123L);
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Repository),
                        Model<Repository>(repository =>
                        {
                            Set(repository, nameof(repository.FullName), "zeeq-ai/zeeq");
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.PullRequest),
                        Model<PullRequestPayload>(pullRequest =>
                        {
                            Set(pullRequest, nameof(pullRequest.Number), 42L);
                            Set(pullRequest, nameof(pullRequest.NodeId), "PR_node");
                            Set(pullRequest, nameof(pullRequest.Title), "Add webhook adapters");
                            Set(
                                pullRequest,
                                nameof(pullRequest.HtmlUrl),
                                "https://github.com/zeeq-ai/zeeq/pull/42"
                            );
                            Set(pullRequest, nameof(pullRequest.Draft), false);
                            Set(
                                pullRequest,
                                nameof(pullRequest.Head),
                                Model<PullRequestHead>(head =>
                                {
                                    Set(head, nameof(head.Ref), "feature");
                                    Set(head, nameof(head.Sha), "abc123");
                                })
                            );
                            Set(
                                pullRequest,
                                nameof(pullRequest.Base),
                                Model<PullRequestBase>(pullRequestBase =>
                                {
                                    Set(pullRequestBase, nameof(pullRequestBase.Ref), "main");
                                })
                            );
                            Set(
                                pullRequest,
                                nameof(pullRequest.User),
                                Model<GitHubUser>(user =>
                                {
                                    Set(user, nameof(user.Login), "octo-user");
                                })
                            );
                        })
                    );
                }),
                PullRequestAction.Opened
            );

        public ValueTask ProcessIssueCommentAsync(
            bool isPullRequest,
            string commentBody = "+zeeq"
        ) =>
            base.ProcessIssueCommentWebhookAsync(
                new WebhookHeaders { Delivery = "delivery-2", Event = "issue_comment" },
                Model<IssueCommentCreatedEvent>(payload =>
                {
                    Set(
                        payload,
                        nameof(payload.Installation),
                        Model<InstallationLite>(installation =>
                        {
                            Set(installation, nameof(installation.Id), 123L);
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Repository),
                        Model<Repository>(repository =>
                        {
                            Set(repository, nameof(repository.FullName), "zeeq-ai/zeeq");
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Issue),
                        Model<Issue>(issue =>
                        {
                            Set(issue, nameof(issue.Number), 42L);
                            Set(issue, nameof(issue.NodeId), "issue_node");
                            Set(
                                issue,
                                nameof(issue.PullRequest),
                                isPullRequest ? Model<IssuePullRequest>(_ => { }) : null
                            );
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Comment),
                        Model<IssueComment>(comment =>
                        {
                            Set(comment, nameof(comment.Id), 9001L);
                            Set(comment, nameof(comment.NodeId), "comment_node");
                            Set(comment, nameof(comment.Body), commentBody);
                            Set(
                                comment,
                                nameof(comment.HtmlUrl),
                                "https://github.com/zeeq-ai/zeeq/pull/42#issuecomment-9001"
                            );
                            Set(
                                comment,
                                nameof(comment.User),
                                Model<GitHubUser>(user =>
                                {
                                    Set(user, nameof(user.Login), "octo-user");
                                })
                            );
                        })
                    );
                }),
                IssueCommentAction.Created
            );

        public ValueTask ProcessReviewCommentAsync(string commentBody = "+zeeq") =>
            base.ProcessPullRequestReviewCommentWebhookAsync(
                new WebhookHeaders
                {
                    Delivery = "delivery-3",
                    Event = "pull_request_review_comment",
                },
                Model<PullRequestReviewCommentCreatedEvent>(payload =>
                {
                    Set(
                        payload,
                        nameof(payload.Installation),
                        Model<InstallationLite>(installation =>
                        {
                            Set(installation, nameof(installation.Id), 123L);
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Repository),
                        Model<Repository>(repository =>
                        {
                            Set(repository, nameof(repository.FullName), "zeeq-ai/zeeq");
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.PullRequest),
                        Model<SimplePullRequestPayload>(pullRequest =>
                        {
                            Set(pullRequest, nameof(pullRequest.Number), 42L);
                            Set(pullRequest, nameof(pullRequest.NodeId), "PR_node");
                        })
                    );
                    Set(
                        payload,
                        nameof(payload.Comment),
                        Model<ReviewCommentPayload>(comment =>
                        {
                            Set(comment, nameof(comment.Id), 7001L);
                            Set(comment, nameof(comment.NodeId), "review_comment_node");
                            Set(comment, nameof(comment.Body), commentBody);
                            Set(
                                comment,
                                nameof(comment.HtmlUrl),
                                "https://github.com/zeeq-ai/zeeq/pull/42#discussion_r7001"
                            );
                            Set(comment, nameof(comment.Path), "src/app.cs");
                            Set(comment, nameof(comment.CommitId), "def456");
                            Set(comment, nameof(comment.PullRequestReviewId), 8001L);
                            Set(
                                comment,
                                nameof(comment.User),
                                Model<GitHubUser>(user =>
                                {
                                    Set(user, nameof(user.Login), "octo-user");
                                })
                            );
                        })
                    );
                }),
                PullRequestReviewCommentAction.Created
            );
    }

    private static T Model<T>(Action<T> configure)
        where T : class
    {
        var model = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        configure(model);

        return model;
    }

    private static void Set<T>(T model, string propertyName, object? value)
        where T : class
    {
        var property = typeof(T).GetProperty(propertyName);
        property?.SetValue(model, value);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NoOpGitHubInstallationStore : IGitHubInstallationStore
    {
        public Task<GitHubAppInstallation?> FindByInstallationIdAsync(
            long installationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<GitHubAppInstallation?>(null);

        public Task<GitHubAppInstallation?> FindActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<GitHubAppInstallation?>(null);

        public Task<GitHubAppInstallation> UpsertLinkedInstallationAsync(
            GitHubAppInstallation installation,
            CancellationToken cancellationToken
        ) => Task.FromResult(installation);

        public Task ApplyLifecycleEventAsync(
            long installationId,
            string repositorySelection,
            DateTimeOffset? suspendedAtUtc,
            DateTimeOffset? deletedAtUtc,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class FakeCodeRepositoryStore(CodeRepository? repository) : ICodeRepositoryStore
    {
        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                repository is not null
                && repository.Provider == provider
                && repository.OwnerQualifiedName == ownerQualifiedName
                    ? repository
                    : null
            );

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class CapturingPublisher : IZeeqMessagePublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync<TMessage>(
            TMessage message,
            CancellationToken cancellationToken = default
        )
            where TMessage : class, IRequest
        {
            Published.Add(message);

            return Task.CompletedTask;
        }

        public Task PublishAfterAsync<TMessage>(
            TMessage message,
            TimeSpan delay,
            CancellationToken cancellationToken = default
        )
            where TMessage : class, IRequest
        {
            Published.Add(message);

            return Task.CompletedTask;
        }
    }
}
