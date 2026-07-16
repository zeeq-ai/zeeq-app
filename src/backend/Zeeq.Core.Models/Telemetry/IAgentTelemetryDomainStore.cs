namespace Zeeq.Core.Models;

/// <summary>
/// Domain write contract for normalized agent telemetry. The processing service
/// uses this to upsert conversations, append events, and acknowledge raw rows in
/// one transaction.
/// </summary>
/// <remarks>
/// Kept as an abstraction so the processing service does not depend on
/// <c>PostgresDbContext</c> directly. Storage providers implement this with
/// provider-specific upsert and partitioning strategies.
///
/// TODO: Phase 9 adds <c>UpsertSessionLinkAsync</c> for PR↔session linking.
/// TODO: Phase 13 replaces the per-conversation existence check with an
/// <c>xmax = 0</c> SQL upsert that returns newly inserted keys in a single
/// round trip, enabling the <c>zeeq_agent_session_counter</c> metric.
/// </remarks>
public interface IAgentTelemetryDomainStore
{
    /// <summary>
    /// Finds conversations in an organization that have the specified head branch.
    /// Implementations must further constrain the result to the canonical repository
    /// identity supplied by the caller.
    /// </summary>
    Task<IReadOnlyList<AgentConversation>> FindForRepositoryBranchAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        string branch,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a pull-request conversation link if it does not already exist.
    /// </summary>
    /// <returns><see langword="true"/> only when a new link was created.</returns>
    Task<bool> TryCreatePullRequestSessionLinkAsync(
        AgentPullRequestSessionLink link,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upserts conversations, appends events, and acknowledges the raw rows that
    /// produced them — all in a single transaction.
    /// </summary>
    /// <param name="conversations">Conversations to upsert (merge on conflict).</param>
    /// <param name="events">Events to append.</param>
    /// <param name="rawRows">Raw rows to delete after successful domain write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Newly inserted domain identities from the transaction.</returns>
    Task<AgentTelemetryDomainWriteResult> UpsertConversationsEventsAndAcknowledgeRawAsync(
        IEnumerable<AgentConversation> conversations,
        IEnumerable<AgentSessionEvent> events,
        IReadOnlyList<TelemetryRawRequest> rawRows,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Resolves normalized telemetry conversations for a persisted pull request.
/// </summary>
public interface IAgentTelemetryPullRequestLinker
{
    /// <summary>
    /// Creates idempotent automated links for conversations on the PR's repository and branch.
    /// </summary>
    Task<int> LinkAsync(PullRequestRecord pullRequest, CancellationToken cancellationToken);
}

/// <summary>
/// Identifies which normalized telemetry rows were newly inserted by a domain write.
/// </summary>
/// <remarks>
/// Metric emitters use this result to count only newly persisted conversations
/// and events. This keeps business metrics idempotent when a raw payload is
/// retried or redelivered after the normalized rows already exist.
/// </remarks>
/// <param name="NewConversationKeys">Conversation keys inserted during the write.</param>
/// <param name="NewEventIds">Event IDs inserted during the write.</param>
public sealed record AgentTelemetryDomainWriteResult(
    IReadOnlySet<AgentConversationKey> NewConversationKeys,
    IReadOnlySet<string> NewEventIds
);
