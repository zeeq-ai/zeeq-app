using System.Text.Json;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres;
using Zeeq.Platform.Telemetry.Processing;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the public raw-telemetry store contract backed by Postgres.
/// </summary>
/// <remarks>
/// The concrete store is intentionally internal. These tests resolve the public
/// <see cref="ITelemetryRawRequestStore"/> through production registration so they
/// exercise its actual dependency-injection boundary as well as lease semantics.
/// </remarks>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[Category("Integration")]
[NotInParallel("telemetry-raw-request-store")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TelemetryRawRequestStoreIntegrationTests(PgDatabaseFixture postgres)
{
    [Test]
    public async Task PullRequestLinker_MatchesOnlyOrganizationRepositoryAndBranch()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var now = DateTimeOffset.UtcNow;
        var conversations = new[]
        {
            Conversation("org-a", "match", "https://github.com/owner/repo.git", "feat/link", now),
            Conversation(
                "org-a",
                "other-repo",
                "https://github.com/owner/other.git",
                "feat/link",
                now
            ),
            Conversation(
                "org-b",
                "other-org",
                "https://github.com/owner/repo.git",
                "feat/link",
                now
            ),
        };
        await store.UpsertConversationsEventsAndAcknowledgeRawAsync(
            conversations,
            [],
            [],
            CancellationToken.None
        );

        var created = await new TelemetryPullRequestLinkingService(store).LinkAsync(
            new PullRequestRecord
            {
                Id = "pr-link-test",
                OrganizationId = "org-a",
                RepositoryId = "repo-a",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 1,
                GitHubNodeId = "node",
                Branch = "feat/link",
                BaseBranch = "main",
                HeadSha = "sha",
                Title = "test",
                AuthorLogin = "author",
                HtmlUrl = "https://github.com/owner/repo/pull/1",
                State = PullRequestState.Open,
                IsDraft = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            CancellationToken.None
        );

        await Assert.That(created).IsEqualTo(1);
        db.ChangeTracker.Clear();

        var persisted = await db.AgentConversations.SingleAsync(row =>
            row.OrganizationId == "org-a" && row.Id == "match"
        );
        await Assert.That(persisted.RepoRemoteUrl).IsEqualTo("owner/repo");
    }

    [Test]
    public async Task PullRequestLinker_InvalidRepositoryIdentityDoesNotMatchInvalidTelemetryRemote()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertConversationsEventsAndAcknowledgeRawAsync(
            [
                Conversation(
                    "org-invalid-repo",
                    "invalid-remote",
                    "not-a-github-remote",
                    "feat/link",
                    now
                ),
            ],
            [],
            [],
            CancellationToken.None
        );

        var matches = await store.FindForRepositoryBranchAsync(
            "org-invalid-repo",
            "not-a-valid-owner-qualified-repo",
            "feat/link",
            CancellationToken.None
        );

        await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task TryCreatePullRequestSessionLinkAsync_ConcurrentDuplicateReturnsFalse()
    {
        var linkKey = Guid.CreateVersion7().ToString("N");
        var links = new[]
        {
            Link($"link-a-{linkKey}", $"org-{linkKey}", $"pr-{linkKey}", $"conversation-{linkKey}"),
            Link($"link-b-{linkKey}", $"org-{linkKey}", $"pr-{linkKey}", $"conversation-{linkKey}"),
        };

        var results = await Task.WhenAll(
            CreatePullRequestSessionLinkAsync(postgres.ConnectionString, links[0]),
            CreatePullRequestSessionLinkAsync(postgres.ConnectionString, links[1])
        );

        await Assert.That(results.Count(result => result)).IsEqualTo(1);
        await Assert.That(results.Count(result => !result)).IsEqualTo(1);
    }

    [Test]
    public async Task StoreClaimAndDelete_WrongLeaseDoesNotAcknowledgeRow()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITelemetryRawRequestStore>();

        var id = await store.StoreLogsAsync(
            payload: [1, 2, 3],
            signalType: TelemetrySignalType.Logs,
            metadata: Metadata(TelemetrySignalType.Logs),
            ingestUserId: "telemetry-user",
            ingestOrganizationId: "telemetry-org"
        );

        var claimed = await store.ClaimBatchAsync(
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(1)
        );
        var row = claimed.Single(raw => raw.Id == id);

        await Assert.That(row.IngestOrganizationId).IsEqualTo("telemetry-org");
        await Assert.That(row.ProcessingLeaseId).IsNotNull();
        await Assert.That(await store.DeleteClaimedAsync(id, "wrong-lease")).IsFalse();
        await Assert.That(await store.DeleteClaimedAsync(id, row.ProcessingLeaseId!)).IsTrue();
    }

    [Test]
    public async Task StoreRawRequest_TableIsUnlogged()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relpersistence
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'zeeq' AND c.relname = 'telemetry_raw_requests'
            """;

        var persistence = Convert.ToString(await command.ExecuteScalarAsync());

        await Assert.That(persistence).IsEqualTo("u");
    }

    [Test]
    public async Task UpsertConversationsEventsAndAcknowledgeRaw_CommitsDomainRowsAndDeletesClaim()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var rawStore = scope.ServiceProvider.GetRequiredService<ITelemetryRawRequestStore>();
        var domainStore = scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var now = DateTimeOffset.UtcNow;

        var rawId = await rawStore.StoreLogsAsync(
            payload: [4, 5, 6],
            signalType: TelemetrySignalType.Logs,
            metadata: Metadata(TelemetrySignalType.Logs),
            ingestUserId: "telemetry-user",
            ingestOrganizationId: "telemetry-org"
        );
        var raw = (await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1))).Single(row =>
            row.Id == rawId
        );
        var conversation = new AgentConversation
        {
            Id = "conversation-commit",
            OrganizationId = "telemetry-org",
            Harness = "codex",
            StartedAtUtc = now,
            CreatedById = "telemetry-user",
            OwnershipStatus = AgentConversationOwnershipStatus.MatchedToIngestPrincipal,
        };
        var sessionEvent = new AgentSessionEvent
        {
            Id = "event-commit",
            OrganizationId = conversation.OrganizationId,
            ConversationId = conversation.Id,
            OccurredAtUtc = now,
            EventType = AgentSessionEventType.Prompt,
            PromptText = "synthetic prompt",
        };
        var toolEvent = new AgentSessionEvent
        {
            Id = "event-commit-tool",
            OrganizationId = conversation.OrganizationId,
            ConversationId = conversation.Id,
            OccurredAtUtc = now.AddMilliseconds(1),
            EventType = AgentSessionEventType.ToolResult,
            ToolName = "mcp__zeeq__search_sections",
            ArgumentsJson = JsonDocument.Parse("""{"query":"telemetry"}"""),
            Success = true,
            DurationMs = 42,
        };

        var created = await domainStore.UpsertConversationsEventsAndAcknowledgeRawAsync(
            [conversation],
            [sessionEvent, Duplicate(sessionEvent), toolEvent],
            [raw],
            CancellationToken.None
        );
        db.ChangeTracker.Clear();

        await Assert
            .That(
                created.NewConversationKeys.Contains(
                    new(conversation.OrganizationId, conversation.Id)
                )
            )
            .IsTrue();
        await Assert.That(created.NewEventIds.Contains(sessionEvent.Id)).IsTrue();
        await Assert.That(created.NewEventIds.Contains(toolEvent.Id)).IsTrue();
        await Assert.That(created.NewEventIds).Count().IsEqualTo(2);
        await Assert
            .That(
                await db.AgentConversations.AnyAsync(row =>
                    row.OrganizationId == conversation.OrganizationId && row.Id == conversation.Id
                )
            )
            .IsTrue();
        await Assert
            .That(
                await db.AgentSessionEvents.AnyAsync(row =>
                    row.OrganizationId == sessionEvent.OrganizationId && row.Id == sessionEvent.Id
                )
            )
            .IsTrue();
        var persistedToolEvent = await db.AgentSessionEvents.SingleAsync(row =>
            row.OrganizationId == toolEvent.OrganizationId && row.Id == toolEvent.Id
        );
        await Assert.That(persistedToolEvent.ToolName).IsEqualTo(toolEvent.ToolName);
        await Assert.That(persistedToolEvent.Success).IsTrue();
        await Assert
            .That(persistedToolEvent.ArgumentsJson?.RootElement.GetProperty("query").GetString())
            .IsEqualTo("telemetry");
        await Assert
            .That(await db.Set<TelemetryRawRequest>().AnyAsync(row => row.Id == rawId))
            .IsFalse();
        await Assert
            .That(await EventPartitionNameAsync(postgres.ConnectionString, sessionEvent.Id))
            .Contains("agent_session_events_p");

        var duplicateRawId = await rawStore.StoreLogsAsync(
            payload: [4, 5, 6],
            signalType: TelemetrySignalType.Logs,
            metadata: Metadata(TelemetrySignalType.Logs),
            ingestUserId: "telemetry-user",
            ingestOrganizationId: "telemetry-org"
        );
        var duplicateRaw = (await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1))).Single(
            row => row.Id == duplicateRawId
        );
        var replayed = await domainStore.UpsertConversationsEventsAndAcknowledgeRawAsync(
            [conversation],
            [sessionEvent],
            [duplicateRaw],
            CancellationToken.None
        );
        db.ChangeTracker.Clear();

        await Assert.That(replayed.NewConversationKeys).IsEmpty();
        await Assert.That(replayed.NewEventIds).IsEmpty();
        await Assert
            .That(
                await db.AgentSessionEvents.CountAsync(row =>
                    row.OrganizationId == sessionEvent.OrganizationId && row.Id == sessionEvent.Id
                )
            )
            .IsEqualTo(1);
        await Assert
            .That(await db.Set<TelemetryRawRequest>().AnyAsync(row => row.Id == duplicateRawId))
            .IsFalse();
    }

    [Test]
    public async Task ReleaseExpiredLeasesAndQuarantine_RespectLeaseOwnership()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var rawStore = scope.ServiceProvider.GetRequiredService<ITelemetryRawRequestStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();

        var expiringId = await rawStore.StoreLogsAsync(
            payload: [7],
            signalType: TelemetrySignalType.Logs,
            metadata: Metadata(TelemetrySignalType.Logs),
            ingestUserId: "telemetry-user",
            ingestOrganizationId: "telemetry-org"
        );
        var expiring = (await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1))).Single(
            row => row.Id == expiringId
        );
        await db.Set<TelemetryRawRequest>()
            .Where(row => row.Id == expiringId)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(
                    row => row.ProcessingLeaseExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddMinutes(-1)
                )
            );

        await Assert.That(await rawStore.ReleaseExpiredLeasesAsync()).IsEqualTo(1);
        var reclaimed = (await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1))).Single(
            row => row.Id == expiringId
        );
        await Assert.That(reclaimed.AttemptCount).IsEqualTo(2);
        await Assert.That(reclaimed.ProcessingLeaseId).IsNotEqualTo(expiring.ProcessingLeaseId);

        await rawStore.QuarantineAsync(
            expiringId,
            processingLeaseId: "wrong-lease",
            reason: "ignored"
        );
        await rawStore.QuarantineAsync(
            expiringId,
            reclaimed.ProcessingLeaseId!,
            reason: "invalid protobuf"
        );
        db.ChangeTracker.Clear();

        var quarantined = await db.Set<TelemetryRawRequest>()
            .SingleAsync(row => row.Id == expiringId);
        await Assert
            .That(quarantined.ProcessingStatus)
            .IsEqualTo(TelemetryRawRequestProcessingStatus.Quarantined);
        await Assert.That(quarantined.QuarantineReason).IsEqualTo("invalid protobuf");
        await Assert.That(await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1))).IsEmpty();
    }

    [Test]
    public async Task UpsertConversations_SameConversationIdInTwoOrganizations_RemainsIsolated()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var rawStore = scope.ServiceProvider.GetRequiredService<ITelemetryRawRequestStore>();
        var domainStore = scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "shared-conversation-id";

        var rawIds = new[]
        {
            await rawStore.StoreLogsAsync(
                payload: [8],
                signalType: TelemetrySignalType.Logs,
                metadata: Metadata(TelemetrySignalType.Logs),
                ingestUserId: "user-a",
                ingestOrganizationId: "org-a"
            ),
            await rawStore.StoreLogsAsync(
                payload: [9],
                signalType: TelemetrySignalType.Logs,
                metadata: Metadata(TelemetrySignalType.Logs),
                ingestUserId: "user-b",
                ingestOrganizationId: "org-b"
            ),
        };
        var rawRows = (await rawStore.ClaimBatchAsync(1_000, TimeSpan.FromMinutes(1)))
            .Where(row => rawIds.Contains(row.Id))
            .ToList();
        var conversations = new[]
        {
            new AgentConversation
            {
                Id = conversationId,
                OrganizationId = "org-a",
                Harness = "codex",
                StartedAtUtc = now,
            },
            new AgentConversation
            {
                Id = conversationId,
                OrganizationId = "org-b",
                Harness = "claude-code",
                StartedAtUtc = now,
            },
        };

        await domainStore.UpsertConversationsEventsAndAcknowledgeRawAsync(
            conversations,
            [],
            rawRows,
            CancellationToken.None
        );
        db.ChangeTracker.Clear();

        await Assert
            .That(
                await db.AgentConversations.CountAsync(row =>
                    row.Id == conversationId
                    && (row.OrganizationId == "org-a" || row.OrganizationId == "org-b")
                )
            )
            .IsEqualTo(2);
        var orgA = await db.AgentConversations.SingleAsync(row =>
            row.Id == conversationId && row.OrganizationId == "org-a"
        );
        var orgB = await db.AgentConversations.SingleAsync(row =>
            row.Id == conversationId && row.OrganizationId == "org-b"
        );
        await Assert.That(orgA.Harness).IsEqualTo("codex");
        await Assert.That(orgB.Harness).IsEqualTo("claude-code");
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPostgres(
            new AppSettings
            {
                Database = new DatabaseSettings { ConnectionString = connectionString },
            }
        );

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static TelemetryRawRequestMetadata Metadata(TelemetrySignalType signalType) =>
        new(
            Source: "telemetry-integration-test",
            Harness: "test-harness",
            RecordCount: 1,
            SignalType: signalType,
            IngestUserId: "telemetry-user"
        );

    private static AgentConversation Conversation(
        string organizationId,
        string id,
        string remote,
        string branch,
        DateTimeOffset startedAtUtc
    ) =>
        new()
        {
            OrganizationId = organizationId,
            Id = id,
            Harness = "copilot-chat",
            RepoRemoteUrl = remote,
            HeadBranch = branch,
            StartedAtUtc = startedAtUtc,
        };

    private static AgentPullRequestSessionLink Link(
        string id,
        string organizationId,
        string pullRequestRecordId,
        string conversationId
    ) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            PullRequestRecordId = pullRequestRecordId,
            ConversationId = conversationId,
            LinkOrigin = AgentSessionLinkOrigin.WebhookCurated,
            LinkedAtUtc = DateTimeOffset.UtcNow,
            IsPending = false,
        };

    private static async Task<bool> CreatePullRequestSessionLinkAsync(
        string connectionString,
        AgentPullRequestSessionLink link
    )
    {
        await using var provider = CreateProvider(connectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();

        return await store.TryCreatePullRequestSessionLinkAsync(link, CancellationToken.None);
    }

    private static AgentSessionEvent Duplicate(AgentSessionEvent source) =>
        new()
        {
            Id = source.Id,
            OrganizationId = source.OrganizationId,
            ConversationId = source.ConversationId,
            OccurredAtUtc = source.OccurredAtUtc,
            SourceSequence = source.SourceSequence,
            SourceRecordId = source.SourceRecordId,
            EventType = source.EventType,
            PromptGroupId = source.PromptGroupId,
            ToolCallId = source.ToolCallId,
            ProviderRequestId = source.ProviderRequestId,
            PromptText = source.PromptText,
            PromptLength = source.PromptLength,
            ToolName = source.ToolName,
            ToolNameRaw = source.ToolNameRaw,
            McpServer = source.McpServer,
            McpServerOrigin = source.McpServerOrigin,
            McpServerScope = source.McpServerScope,
            ArgumentsJson = source.ArgumentsJson,
            OutputSnippet = source.OutputSnippet,
            Success = source.Success,
            DurationMs = source.DurationMs,
            Decision = source.Decision,
            DecisionSource = source.DecisionSource,
            Model = source.Model,
            InputTokens = source.InputTokens,
            CachedTokens = source.CachedTokens,
            OutputTokens = source.OutputTokens,
            ReasoningTokens = source.ReasoningTokens,
            ToolTokens = source.ToolTokens,
            CostUsd = source.CostUsd,
            CostSource = source.CostSource,
            CostUnitsRaw = source.CostUnitsRaw,
            QuerySource = source.QuerySource,
            IsHousekeeping = source.IsHousekeeping,
        };

    private static async Task<string> EventPartitionNameAsync(
        string connectionString,
        string eventId
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tableoid::regclass::text
            FROM zeeq.agent_session_events
            WHERE id = @event_id
            """;
        command.Parameters.AddWithValue("event_id", eventId);

        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
