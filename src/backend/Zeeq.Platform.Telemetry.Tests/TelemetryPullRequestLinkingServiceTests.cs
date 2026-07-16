using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Processing;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Tests branch-first pull-request association independently of webhook delivery.
/// </summary>
/// <remarks>
/// Run with:
/// <c>dotnet run --project src/backend/Zeeq.Platform.Telemetry.Tests --output detailed --disable-logo --treenode-filter "/*/*/TelemetryPullRequestLinkingServiceTests/*"</c>.
///
/// This class is marked <see cref="NotInParallelAttribute"/> because the PR-link
/// metric test uses a <see cref="MeterListener"/> attached to the process-global
/// <see cref="ZeeqTelemetry.Metrics"/> meter. Parallel metric tests can observe
/// each other's emissions, so this class runs serially to keep the captured
/// measurement set scoped to the test action.
/// </remarks>
[Category("Unit")]
[NotInParallel]
public sealed class TelemetryPullRequestLinkingServiceTests
{
    [Test]
    public async Task LinkAsync_CreatesOneLinkForEachConversationOnTheBranch()
    {
        // Guards that branch-first PR association links every matching conversation and stamps
        // automated webhook links as confirmed, non-user-curated records.
        var store = new TestDomainStore(
            Conversation("conversation-a"),
            Conversation("conversation-b")
        );
        var service = new TelemetryPullRequestLinkingService(store);

        var created = await service.LinkAsync(PullRequest(), CancellationToken.None);

        await Assert.That(created).IsEqualTo(2);

        await Assert
            .That(store.CreatedLinks.Select(link => link.ConversationId))
            .IsEquivalentTo(["conversation-a", "conversation-b"]);
        await Assert
            .That(
                store.CreatedLinks.All(link =>
                    link.LinkOrigin == AgentSessionLinkOrigin.WebhookCurated
                    && !link.IsPending
                    && link.LinkedByUserId is null
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task LinkAsync_Redelivery_DoesNotCreateDuplicateLinks()
    {
        // Guards webhook redelivery idempotency: the same PR/conversation pair can be observed
        // multiple times, but the domain store creates at most one link.
        var store = new TestDomainStore(Conversation("conversation-a"));
        var service = new TelemetryPullRequestLinkingService(store);

        await Assert
            .That(await service.LinkAsync(PullRequest(), CancellationToken.None))
            .IsEqualTo(1);
        await Assert
            .That(await service.LinkAsync(PullRequest(), CancellationToken.None))
            .IsEqualTo(0);
        await Assert.That(store.CreatedLinks).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LinkAsync_EmitsMetricOnlyForNewLinks()
    {
        // Guards that PR-link business metrics count newly persisted links only; redelivered
        // webhooks should not inflate shipped-work attribution.
        var store = new TestDomainStore(Conversation("conversation-a"));
        var service = new TelemetryPullRequestLinkingService(store);
        var measurements = new List<CapturedMeasurement>();

        using var listener = CaptureAgentMetrics(measurements);

        await Assert
            .That(await service.LinkAsync(PullRequest(), CancellationToken.None))
            .IsEqualTo(1);
        await Assert
            .That(await service.LinkAsync(PullRequest(), CancellationToken.None))
            .IsEqualTo(0);

        var link = measurements.Single(m =>
            m.InstrumentName == AgentTelemetryMetrics.PullRequestLinkCounterName
        );

        await Assert.That(link.Value).IsEqualTo(1);
        await Assert.That(link.Tags["organization_id"]).IsEqualTo("org_123");
        await Assert.That(link.Tags["harness"]).IsEqualTo("codex");
        await Assert.That(link.Tags["link_origin"]).IsEqualTo("WebhookCurated");
    }

    [Test]
    public async Task Normalize_GitHubHttpsAndSshRemotes_UsesSameRepositoryIdentity()
    {
        // Guards that repository matching is canonical across owner/repo shorthand, HTTPS, and
        // SSH remotes so branch association does not depend on a harness-specific URL shape.
        await Assert
            .That(TelemetryRepositoryIdentity.Normalize("owner/repo"))
            .IsEqualTo("owner/repo");
        await Assert
            .That(TelemetryRepositoryIdentity.Normalize("https://github.com/Owner/Repo.git"))
            .IsEqualTo("owner/repo");
        await Assert
            .That(TelemetryRepositoryIdentity.Normalize("git@github.com:Owner/Repo.git"))
            .IsEqualTo("owner/repo");
    }

    [Test]
    public async Task ResolveConversationId_WithoutHarnessSession_UsesStableBranchScopedFallback()
    {
        // Guards that harnesses without stable session IDs still resolve to one deterministic
        // repository-and-branch-scoped conversation identity.
        var first = TelemetryRepositoryIdentity.ResolveConversationId(
            null,
            "git@github.com:Owner/Repo.git",
            "feat/branch-first-linking"
        );
        var second = TelemetryRepositoryIdentity.ResolveConversationId(
            null,
            "https://github.com/owner/repo.git",
            "feat/branch-first-linking"
        );

        await Assert.That(first).StartsWith("branch:");
        await Assert.That(second).IsEqualTo(first);
    }

    private static PullRequestRecord PullRequest() =>
        new()
        {
            Id = "pr_123",
            OrganizationId = "org_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "owner/repo",
            PullRequestNumber = 123,
            GitHubNodeId = "PR_node_123",
            Branch = "feat/branch-first-linking",
            BaseBranch = "main",
            HeadSha = "abc123",
            Title = "Link telemetry",
            AuthorLogin = "octo",
            HtmlUrl = "https://github.com/owner/repo/pull/123",
            State = PullRequestState.Open,
            IsDraft = false,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static AgentConversation Conversation(string id) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            Harness = "codex",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class TestDomainStore(params AgentConversation[] conversations)
        : IAgentTelemetryDomainStore
    {
        private readonly HashSet<(
            string OrganizationId,
            string PullRequestId,
            string ConversationId
        )> keys = [];

        public List<AgentPullRequestSessionLink> CreatedLinks { get; } = [];

        public Task<IReadOnlyList<AgentConversation>> FindForRepositoryBranchAsync(
            string organizationId,
            string ownerQualifiedRepoName,
            string branch,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<AgentConversation>>(conversations);

        public Task<bool> TryCreatePullRequestSessionLinkAsync(
            AgentPullRequestSessionLink link,
            CancellationToken cancellationToken
        )
        {
            if (!keys.Add((link.OrganizationId, link.PullRequestRecordId, link.ConversationId)))
            {
                return Task.FromResult(false);
            }

            CreatedLinks.Add(link);
            return Task.FromResult(true);
        }

        public Task<AgentTelemetryDomainWriteResult> UpsertConversationsEventsAndAcknowledgeRawAsync(
            IEnumerable<AgentConversation> conversations,
            IEnumerable<AgentSessionEvent> events,
            IReadOnlyList<TelemetryRawRequest> rawRows,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<AgentTelemetryDomainWriteResult>(
                new(
                    new HashSet<AgentConversationKey>(),
                    new HashSet<string>(StringComparer.Ordinal)
                )
            );
    }

    private static MeterListener CaptureAgentMetrics(List<CapturedMeasurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (
                ReferenceEquals(instrument.Meter, ZeeqTelemetry.Metrics)
                && instrument.Name.StartsWith("zeeq_agent_", StringComparison.Ordinal)
            )
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) =>
                measurements.Add(CapturedMeasurement.From(instrument.Name, value, tags))
        );
        listener.Start();

        return listener;
    }

    private sealed record CapturedMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, string?> Tags
    )
    {
        public static CapturedMeasurement From(
            string instrumentName,
            int value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        )
        {
            var capturedTags = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var tag in tags)
            {
                capturedTags[tag.Key] = tag.Value?.ToString();
            }

            return new(instrumentName, value, capturedTags);
        }
    }
}
