using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Resource.V1;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Cluster-leased background service: processes raw telemetry rows into
/// normalized domain rows.
/// </summary>
/// <remarks>
/// Flow (per batch-claim cycle):
/// 1. Release expired leases (crash recovery while Postgres is available).
/// 2. Claim a batch of raw rows with a unique lease.
/// 3. Parse protobuf → select adapter (harness identity) → adapt → domain write.
/// 4. Apply cost enrichment after adapter dispatch.
/// 5. Upsert conversation (merge: widen timestamps, fill empty, ownership-only upgrade).
/// 6. Append event rows to <c>agent_session_events</c> (partitioned).
/// 7. Commit normalized rows, then delete the raw rows using their current lease.
/// 8. Quarantine terminal parse/adapter failures without blocking later rows.
/// </remarks>
/// <remarks>
/// Creates the processing service with required dependencies.
/// </remarks>
public sealed class TelemetryProcessingService(
    IServiceScopeFactory scopeFactory,
    IAgentTelemetryCostEnricher costEnricher,
    TelemetrySettings settings,
    ILogger<TelemetryProcessingService> log
) : BackgroundService
{
    private static readonly string WorkerId = "telproc-" + ProcessingWorkerId.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var rawStore =
                    scope.ServiceProvider.GetRequiredService<ITelemetryRawRequestStore>();
                var domainStore =
                    scope.ServiceProvider.GetRequiredService<IAgentTelemetryDomainStore>();
                var adapters = scope.ServiceProvider.GetRequiredService<
                    IEnumerable<IAgentTelemetryAdapter>
                >();

                await rawStore.ReleaseExpiredLeasesAsync(stoppingToken);

                var batch = await rawStore.ClaimBatchAsync(
                    settings.IngestBatchSize,
                    TimeSpan.FromSeconds(settings.LeaseTtlSeconds),
                    stoppingToken
                );

                if (batch.Count == 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(settings.ProcessingPollIntervalMs),
                        stoppingToken
                    );

                    continue;
                }

                await ProcessBatchAsync(rawStore, domainStore, adapters, batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unhandled exception in telemetry processing loop");

                await Task.Delay(
                    TimeSpan.FromMilliseconds(settings.ProcessingPollIntervalMs),
                    stoppingToken
                );
            }
        }
    }

    private async Task ProcessBatchAsync(
        ITelemetryRawRequestStore rawStore,
        IAgentTelemetryDomainStore domainStore,
        IEnumerable<IAgentTelemetryAdapter> adapters,
        IReadOnlyList<TelemetryRawRequest> batch,
        CancellationToken ct
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity("telemetry.process.batch");
        activity?.SetTag("telemetry.batch_size", batch.Count);

        var conversations = new Dictionary<AgentConversationKey, AgentConversation>();
        var events = new List<AgentSessionEvent>();
        var successfullyAdapted = new List<TelemetryRawRequest>();

        // NOTE: The AdaptRaw → dictionary fold path creates intermediate conversation
        // objects that are de-duplicated and re-merged. A future optimization could
        // fold directly into the dictionary on first pass, reducing allocation.

        foreach (var raw in batch)
        {
            try
            {
                var (adaptedConversations, adaptedEvents) = AdaptRaw(raw, adapters);

                if (adaptedConversations.Count == 0)
                {
                    // NOTE: Empty-adaptation raw rows are acknowledged directly (no
                    // domain work to transact). This splits the ack path, but the
                    // alternative — including empty rows in the domain transaction —
                    // would unnecessarily entangle the store contract with no-op rows.
                    // Unknown harness records pass through the defensive filter but
                    // produce no adapter matches; they are correctly deleted here and
                    // will not be retried.
                    // the domain transaction since there is nothing to transact.
                    await rawStore.DeleteClaimedAsync(raw.Id, raw.ProcessingLeaseId!, ct);

                    continue;
                }

                foreach (var c in adaptedConversations)
                {
                    var key = new AgentConversationKey(c.OrganizationId, c.Id);

                    if (!conversations.TryGetValue(key, out var existing))
                    {
                        conversations[key] = c;
                    }
                    else
                    {
                        existing.MergeFrom(c);
                    }
                }

                events.AddRange(adaptedEvents);

                successfullyAdapted.Add(raw);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error adapting raw row {RawRowId}; quarantining", raw.Id);

                await rawStore.QuarantineAsync(raw.Id, raw.ProcessingLeaseId!, ex.Message, ct);
            }
        }

        if (conversations.Count == 0)
        {
            return;
        }

        var writeResult = await domainStore.UpsertConversationsEventsAndAcknowledgeRawAsync(
            conversations.Values,
            events,
            successfullyAdapted,
            ct
        );
        activity?.SetTag("telemetry.conversations_upserted", conversations.Count);
        activity?.SetTag("telemetry.events_appended", events.Count);

        foreach (var key in writeResult.NewConversationKeys)
        {
            if (conversations.TryGetValue(key, out var conversation))
            {
                AgentTelemetryMetrics.RecordNewSession(conversation);
            }
        }

        var newEventIds = new HashSet<string>(writeResult.NewEventIds, StringComparer.Ordinal);

        foreach (
            var sessionEvent in events.Where(e => newEventIds.Contains(e.Id)).DistinctBy(e => e.Id)
        )
        {
            var key = new AgentConversationKey(
                sessionEvent.OrganizationId,
                sessionEvent.ConversationId
            );

            if (conversations.TryGetValue(key, out var conversation))
            {
                AgentTelemetryMetrics.RecordEvent(conversation, sessionEvent);
            }
        }
    }

    private (
        IReadOnlyList<AgentConversation> Conversations,
        IReadOnlyList<AgentSessionEvent> Events
    ) AdaptRaw(TelemetryRawRequest raw, IEnumerable<IAgentTelemetryAdapter> adapters)
    {
        var conversations = new List<AgentConversation>();
        var events = new List<AgentSessionEvent>();

        if (raw.SignalType == TelemetrySignalType.Logs)
        {
            var request =
                OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest.Parser.ParseFrom(
                    raw.Payload
                );

            foreach (var ctx in TelemetryLogRecordContext.Enumerate(request))
            {
                var adapter = adapters.FirstOrDefault(a => a.CanHandle(ctx));
                if (adapter is null)
                {
                    continue;
                }

                var result = adapter.Adapt(ctx);

                var observedAtUtc = result.Event?.OccurredAtUtc ?? raw.ReceivedAtUtc;

                var conversation = new AgentConversation
                {
                    Id = result.Conversation.ConversationId,
                    OrganizationId = raw.IngestOrganizationId ?? "",
                    Harness = result.Conversation.Harness,
                    HarnessVariant = result.Conversation.HarnessVariant,
                    AppVersion = result.Conversation.AppVersion,
                    RepoRemoteUrl = CanonicalRepositoryOrNull(result.Conversation.RepoRemoteUrl),
                    HeadBranch = result.Conversation.HeadBranch,
                    HeadSha = result.Conversation.HeadSha,
                    OwnerEmail = result.Conversation.OwnerEmail,
                    StartedAtUtc = observedAtUtc,
                    OwnershipStatus = AgentConversationOwnershipStatus.MatchedToIngestPrincipal,
                    CreatedById = raw.IngestUserId,
                };

                conversations.Add(conversation);

                if (result.Event is { } evt)
                {
                    var enriched = costEnricher.Enrich(evt, adapter.HarnessName);

                    events.Add(
                        new AgentSessionEvent
                        {
                            Id = EventId(
                                evt,
                                raw.IngestOrganizationId,
                                result.Conversation.ConversationId
                            ),
                            OrganizationId = raw.IngestOrganizationId ?? "",
                            ConversationId = result.Conversation.ConversationId,
                            OccurredAtUtc = observedAtUtc,
                            SourceSequence = evt.SourceSequence,
                            SourceRecordId = evt.SourceRecordId,
                            EventType = evt.EventType,
                            PromptGroupId = evt.PromptGroupId,
                            ToolCallId = evt.ToolCallId,
                            ProviderRequestId = evt.ProviderRequestId,
                            PromptText = evt.PromptText,
                            PromptLength = evt.PromptLength,
                            ToolName = evt.ToolName,
                            ToolNameRaw = evt.ToolNameRaw,
                            McpServer = evt.McpServer,
                            McpServerOrigin = evt.McpServerOrigin,
                            McpServerScope = evt.McpServerScope,
                            ArgumentsJson = evt.ArgumentsJson,
                            OutputSnippet = evt.OutputSnippet,
                            Success = evt.Success,
                            DurationMs = evt.DurationMs,
                            Decision = evt.Decision,
                            DecisionSource = evt.DecisionSource,
                            Model = evt.Model,
                            InputTokens = evt.InputTokens,
                            CachedTokens = evt.CachedTokens,
                            OutputTokens = evt.OutputTokens,
                            ReasoningTokens = evt.ReasoningTokens,
                            ToolTokens = evt.ToolTokens,
                            CostUsd = enriched.CostUsd,
                            CostSource = enriched.CostSource,
                            CostUnitsRaw = evt.CostUnitsRaw,
                            QuerySource = evt.QuerySource,
                            IsHousekeeping = evt.IsHousekeeping,
                        }
                    );
                }
            }
        }
        else if (raw.SignalType == TelemetrySignalType.Traces)
        {
            var request =
                OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest.Parser.ParseFrom(
                    raw.Payload
                );

            foreach (var resourceSpan in request.ResourceSpans)
            {
                foreach (var scopeSpan in resourceSpan.ScopeSpans)
                {
                    foreach (var span in scopeSpan.Spans)
                    {
                        var ctx = new TelemetrySpanRecordContext(
                            span,
                            resourceSpan.Resource ?? new Resource()
                        );
                        var adapter = adapters.FirstOrDefault(a => a.CanHandle(ctx));
                        if (adapter is null)
                        {
                            continue;
                        }

                        var result = adapter.Adapt(ctx);

                        var observedAtUtc = result.Event?.OccurredAtUtc ?? raw.ReceivedAtUtc;

                        var conversation = new AgentConversation
                        {
                            Id = result.Conversation.ConversationId,
                            OrganizationId = raw.IngestOrganizationId ?? "",
                            Harness = result.Conversation.Harness,
                            HarnessVariant = result.Conversation.HarnessVariant,
                            AppVersion = result.Conversation.AppVersion,
                            RepoRemoteUrl = CanonicalRepositoryOrNull(
                                result.Conversation.RepoRemoteUrl
                            ),
                            HeadBranch = result.Conversation.HeadBranch,
                            HeadSha = result.Conversation.HeadSha,
                            OwnerEmail = result.Conversation.OwnerEmail,
                            StartedAtUtc = observedAtUtc,
                            OwnershipStatus =
                                AgentConversationOwnershipStatus.MatchedToIngestPrincipal,
                            CreatedById = raw.IngestUserId,
                        };
                        conversations.Add(conversation);

                        if (result.Event is { } evt)
                        {
                            var enriched = costEnricher.Enrich(evt, adapter.HarnessName);

                            events.Add(
                                new AgentSessionEvent
                                {
                                    Id = EventId(
                                        evt,
                                        raw.IngestOrganizationId,
                                        result.Conversation.ConversationId
                                    ),
                                    OrganizationId = raw.IngestOrganizationId ?? "",
                                    ConversationId = result.Conversation.ConversationId,
                                    OccurredAtUtc = observedAtUtc,
                                    SourceSequence = evt.SourceSequence,
                                    SourceRecordId = evt.SourceRecordId,
                                    EventType = evt.EventType,
                                    PromptGroupId = evt.PromptGroupId,
                                    ToolCallId = evt.ToolCallId,
                                    ProviderRequestId = evt.ProviderRequestId,
                                    PromptText = evt.PromptText,
                                    PromptLength = evt.PromptLength,
                                    ToolName = evt.ToolName,
                                    ToolNameRaw = evt.ToolNameRaw,
                                    McpServer = evt.McpServer,
                                    McpServerOrigin = evt.McpServerOrigin,
                                    McpServerScope = evt.McpServerScope,
                                    ArgumentsJson = evt.ArgumentsJson,
                                    OutputSnippet = evt.OutputSnippet,
                                    Success = evt.Success,
                                    DurationMs = evt.DurationMs,
                                    Decision = evt.Decision,
                                    DecisionSource = evt.DecisionSource,
                                    Model = evt.Model,
                                    InputTokens = evt.InputTokens,
                                    CachedTokens = evt.CachedTokens,
                                    OutputTokens = evt.OutputTokens,
                                    ReasoningTokens = evt.ReasoningTokens,
                                    ToolTokens = evt.ToolTokens,
                                    CostUsd = enriched.CostUsd,
                                    CostSource = enriched.CostSource,
                                    CostUnitsRaw = evt.CostUnitsRaw,
                                    QuerySource = evt.QuerySource,
                                    IsHousekeeping = evt.IsHousekeeping,
                                }
                            );
                        }
                    }
                }
            }
        }

        return (conversations, events);
    }

    private static string? CanonicalRepositoryOrNull(string? repositoryRemoteUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl))
        {
            return null;
        }

        var repository = TelemetryRepositoryIdentity.Normalize(repositoryRemoteUrl);

        return repository.Length == 0 ? null : repository;
    }

    /// <summary>
    /// Derives a deterministic event identifier from source identity fields so
    /// reprocessing the same raw row (after lease expiry or transaction retry)
    /// produces the same event ID, preventing duplicate insertions.
    /// </summary>
    private static string EventId(
        AgentSessionEventRecord evt,
        string? organizationId,
        string conversationId
    )
    {
        var input =
            $"{organizationId}|{conversationId}|{(byte)evt.EventType}|{evt.SourceSequence}|{evt.SourceRecordId}|{evt.PromptGroupId}|{evt.OccurredAtUtc?.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hash)[..32];
    }
}
