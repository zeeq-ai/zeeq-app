using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Postgres read store for the Sessions inbox: recent conversations and one
/// conversation's prompt/usage detail.
/// </summary>
/// <remarks>
/// Mirrors <c>PostgresPullRequestRecordStore.ListRecentAsync</c>'s seek-cursor
/// shape. Unlike the PR inbox's GitHub-alias join, "Mine" here resolves a
/// small, precomputed set of normalized email keys (the subject's own
/// <c>core_users.email</c> plus their active <see cref="UserAliasKind.Email"/>
/// aliases) so the filter stays a simple <c>Contains</c> rather than a join —
/// there are at most a handful of keys per user.
/// </remarks>
internal sealed class PostgresAgentConversationQueryStore(PostgresDbContext db)
    : IAgentConversationQueryStore
{
    /// <summary>
    /// Inbox rows below this cost are hidden by default — trivial/test conversations (a
    /// couple of prompts, no real work) are mostly noise in a list meant for reviewing what an
    /// agent actually did. Detail links are unaffected: <see cref="GetDetailAsync"/> stays
    /// unscoped, so a direct link to a cheap conversation still works.
    /// </summary>
    private const decimal DefaultMinimumCostUsd = 0.10m;

    /// <summary>
    /// Shared projection so list and detail queries stay in lockstep as summary fields change.
    /// </summary>
    private static readonly Expression<
        Func<AgentConversation, AgentConversationSummary>
    > ToSummary = conversation => new AgentConversationSummary(
        conversation.Id,
        conversation.Harness,
        conversation.HarnessVariant,
        conversation.RepoRemoteUrl,
        conversation.HeadBranch,
        conversation.OwnerEmail,
        conversation.CreatedById,
        conversation.StartedAtUtc,
        conversation.CompletedAtUtc,
        conversation.Title,
        conversation.RollupVersion == AgentConversationRollupVersion.Current
            ? AgentConversationRollupStatus.Ready
            : AgentConversationRollupStatus.Recomputing,
        conversation.RollupVersion == AgentConversationRollupVersion.Current
            ? conversation.TotalInputTokens
            : null,
        conversation.RollupVersion == AgentConversationRollupVersion.Current
            ? conversation.TotalOutputTokens
            : null,
        conversation.RollupVersion == AgentConversationRollupVersion.Current
            ? conversation.TotalCostUsd
            : null
    );

    /// <inheritdoc />
    /// <remarks>
    /// Fetches <c>pageSize + 1</c> rows as a sentinel so <c>NextCursor</c> is only ever
    /// non-null when a subsequent page actually exists (see the sentinel-row comment below).
    /// </remarks>
    public async Task<AgentConversationStreamPage> ListRecentAsync(
        AgentConversationStreamQuery query,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(query.SubjectUserId))
        {
            throw new InvalidOperationException(
                "Conversation stream queries require a subject user id."
            );
        }

        var emailKeys = await OwnerEmailMatchKeysAsync(
            query.OrganizationId,
            query.SubjectUserId,
            cancellationToken
        );

        // NOTE: Trim().ToLower() on OwnerEmail can defeat a plain btree index on that column
        // as this table grows — Postgres won't use an index for a function-wrapped predicate
        // unless a matching expression index exists. Fixing this needs either a persisted
        // normalized-email column (populated at ingest) or a
        // `CREATE INDEX ... (lower(trim(owner_email)))` expression index — a migration, out
        // of scope here. Revisit if this query shows up in slow-query logs as
        // agent_conversations grows.
        // 👈 Intentionally subject-scoped — this is a listing, not the detail fetch.
        // See IAgentConversationQueryStore's remarks: GetDetailAsync below is the unscoped
        // one that makes link-sharing work; this filter must stay.
        var rows = db
            .AgentConversations.TagWithOperationCallSite("agent_conversation.list_recent")
            .Where(conversation => conversation.OrganizationId == query.OrganizationId)
            .Where(conversation =>
                conversation.CreatedById == query.SubjectUserId
                || (
                    conversation.OwnerEmail != null
                    && emailKeys.Contains(conversation.OwnerEmail.Trim().ToLower())
                )
            );

        if (query.MinimumCostUsd is { } requestedMinimumCostUsd)
        {
            var minimumCostUsd =
                requestedMinimumCostUsd == 0 ? DefaultMinimumCostUsd : requestedMinimumCostUsd;

            rows = rows.Where(conversation =>
                conversation.RollupVersion == AgentConversationRollupVersion.Current
                && conversation.TotalCostUsd >= minimumCostUsd
            );
        }
        else
        {
            // A recomputing row's stored total can be stale/incomplete, so it's never enough
            // on its own to hide the row — same for a Ready row with no priced events yet
            // (null). Only a *current, known* total below the default floor hides the row.
            rows = rows.Where(conversation =>
                conversation.RollupVersion != AgentConversationRollupVersion.Current
                || conversation.TotalCostUsd == null
                || conversation.TotalCostUsd >= DefaultMinimumCostUsd
            );
        }

        if (query.Cursor is { } cursor)
        {
            // Seek pagination: continue strictly older than the last rendered row.
            rows = rows.Where(conversation =>
                conversation.StartedAtUtc < cursor.StartedAtUtc
                || (
                    conversation.StartedAtUtc == cursor.StartedAtUtc
                    && string.Compare(conversation.Id, cursor.Id) < 0
                )
            );
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Fetch one extra row as a sentinel: only its presence tells us whether another
        // page exists, so a full-but-final page doesn't emit a cursor that would otherwise
        // send the client on one guaranteed-empty "load more" request.
        var rowsWithSentinel = await rows.OrderByDescending(conversation =>
                conversation.StartedAtUtc
            )
            .ThenByDescending(conversation => conversation.Id)
            .Take(pageSize + 1)
            .Select(ToSummary)
            .ToArrayAsync(cancellationToken);

        var hasMore = rowsWithSentinel.Length > pageSize;
        var items = hasMore ? rowsWithSentinel[..pageSize] : rowsWithSentinel;
        var last = items.LastOrDefault();

        return new AgentConversationStreamPage(
            items,
            hasMore && last is not null
                ? new AgentConversationStreamCursor(last.StartedAtUtc, last.Id)
                : null
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// Three reads, not one fetch split in memory: prompts (capped to the newest 500),
    /// completions bounded to that same prompt window (for per-turn attribution only), and a
    /// SQL-side <c>GROUP BY</c> aggregate over the *entire* conversation's completions (for
    /// cost/token totals and the distinct-models list). The aggregate is what keeps totals
    /// authoritative without materializing every completion event into request memory — see
    /// <see cref="AgentCompletionModelAggregate"/>.
    ///
    /// NOTE: deliberately not ownership-filtered — organization + conversation id is the only
    /// scope, so a direct link to this conversation works for any org member. Do not add a
    /// <c>CreatedById</c>/owner-email check here; that belongs on the inbox listing, not detail.
    /// </remarks>
    public async Task<AgentConversationDetail?> GetDetailAsync(
        string organizationId,
        string conversationId,
        CancellationToken cancellationToken
    )
    {
        var conversation = await db
            .AgentConversations.TagWithOperationCallSite("agent_conversation.get_detail")
            .AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Id == conversationId)
            .Select(ToSummary)
            .FirstOrDefaultAsync(cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        // All three queries below bound OccurredAtUtc >= StartedAtUtc for partition pruning
        // (the same lower-bound pattern PostgresCodeReviewRecordStore uses with
        // PullRequestCreatedAtUtc), plus an upper bound at CompletedAtUtc when the
        // conversation has finished so the planner can prune partitions above the
        // conversation's end too, not just below its start. A still-active conversation
        // (CompletedAtUtc == null) has no upper bound yet.
        //
        // NOTE: this cap can be partly consumed by Claude Code's synthetic <task-notification>
        // pings, since they're only filtered client-side (session-display.ts) after this query
        // already ran — see the TODO on AdaptPrompt in ClaudeCodeTelemetryAdapter.cs for the
        // full explanation and the fix (classify at ingest, filter here once that exists).
        var newestPromptsDescending = await db
            .AgentSessionEvents.TagWithOperationCallSite("agent_conversation.get_detail.prompts")
            .AsNoTracking()
            .Where(e =>
                e.OrganizationId == organizationId
                && e.ConversationId == conversationId
                && e.OccurredAtUtc >= conversation.StartedAtUtc
                && (
                    conversation.CompletedAtUtc == null
                    || e.OccurredAtUtc <= conversation.CompletedAtUtc
                )
                && e.EventType == AgentSessionEventType.Prompt
            )
            // Newest-first + Take, not oldest-first: for a conversation exceeding the cap, the
            // recent end is what a user opening it almost always cares about, and it keeps the
            // last prompt in the returned set genuinely the conversation's last prompt (so
            // AttachTurnTokens' unbounded final window is never wrong — see its remarks).
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(500)
            .Select(e => new AgentConversationPromptEvent(
                e.Id,
                e.OccurredAtUtc,
                e.PromptGroupId,
                e.PromptText,
                e.PromptLength,
                null,
                null
            ))
            .ToArrayAsync(cancellationToken);

        // Re-sort ascending for display and for AttachTurnTokens' single forward sweep — cheap
        // in memory since this array is capped at 500.
        var rawPrompts = newestPromptsDescending.OrderBy(p => p.OccurredAtUtc).ToArray();

        // Bounded to the visible prompt window (>= the oldest *kept* prompt), not the whole
        // conversation: this is only for per-turn token attribution, which by definition can't
        // land on a trimmed-off prompt anyway (AttachTurnTokens already skips anything earlier
        // — see its remarks). Scales with how many completions happened during the visible
        // window, not with the conversation's total history, unlike the aggregate query below.
        var completionsForTurnAttribution =
            rawPrompts.Length == 0
                ? []
                : await db
                    .AgentSessionEvents.TagWithOperationCallSite(
                        "agent_conversation.get_detail.completions_for_turns"
                    )
                    .AsNoTracking()
                    .Where(e =>
                        e.OrganizationId == organizationId
                        && e.ConversationId == conversationId
                        && e.OccurredAtUtc >= rawPrompts[0].OccurredAtUtc
                        && (
                            conversation.CompletedAtUtc == null
                            || e.OccurredAtUtc <= conversation.CompletedAtUtc
                        )
                        && e.EventType == AgentSessionEventType.Completion
                    )
                    .OrderBy(e => e.OccurredAtUtc)
                    .Select(e => new AgentCompletionEventForUsage(
                        e.Model,
                        e.InputTokens,
                        e.CachedTokens,
                        e.OutputTokens,
                        e.ReasoningTokens,
                        e.ToolTokens,
                        e.CostUsd,
                        e.OccurredAtUtc
                    ))
                    .ToArrayAsync(cancellationToken);

        var prompts = AttachTurnTokens(rawPrompts, completionsForTurnAttribution);

        // Unbounded over the whole conversation, but only per-model sums/counts/maxes ever
        // cross into app memory (typically 1-3 rows) — this is what makes the usage/cost
        // summary authoritative regardless of the prompt cap above, without loading every
        // completion event the conversation ever had.
        var usageAggregates = await db
            .AgentSessionEvents.TagWithOperationCallSite(
                "agent_conversation.get_detail.usage_aggregates"
            )
            .AsNoTracking()
            .Where(e =>
                e.OrganizationId == organizationId
                && e.ConversationId == conversationId
                && e.OccurredAtUtc >= conversation.StartedAtUtc
                && (
                    conversation.CompletedAtUtc == null
                    || e.OccurredAtUtc <= conversation.CompletedAtUtc
                )
                && e.EventType == AgentSessionEventType.Completion
            )
            .GroupBy(e => e.Model)
            .Select(g => new AgentCompletionModelAggregate(
                g.Key,
                g.Count(),
                g.Sum(e => (long)(e.InputTokens ?? 0)),
                g.Sum(e => (long)(e.CachedTokens ?? 0)),
                g.Sum(e => (long)(e.OutputTokens ?? 0)),
                g.Sum(e => (long)(e.ReasoningTokens ?? 0)),
                g.Sum(e => (long)(e.ToolTokens ?? 0)),
                g.Sum(e => e.CostUsd ?? 0m),
                g.Count(e => e.CostUsd == null),
                g.Max(e => e.InputTokens ?? 0),
                g.Max(e => e.CachedTokens ?? 0)
            ))
            .ToArrayAsync(cancellationToken);

        return new AgentConversationDetail(conversation, prompts, usageAggregates);
    }

    /// <summary>
    /// Buckets each completion event into the prompt whose turn it belongs to, by time window,
    /// and attaches the per-turn token sums onto that prompt.
    /// </summary>
    /// <remarks>
    /// <c>PromptGroupId</c> would be the precise join key, but it's populated inconsistently
    /// across harnesses — observed 100% <see langword="null"/> on completion events for Codex
    /// and Pi, only Claude Code reliably sets it — so it can't be trusted as the correlation
    /// key. Time windows are harness-agnostic: every completion between one prompt and the
    /// next is that prompt's turn. Both inputs are already ordered ascending by
    /// <c>OccurredAtUtc</c> from their queries above, so this is a single O(n) sweep, not a
    /// nested loop.
    ///
    /// <paramref name="prompts"/> is capped to the newest 500 (see the store's query above).
    /// <paramref name="completionEvents"/> is bounded to the same window in the common case,
    /// but the skip-forward below is kept anyway as a defensive tie-break at the boundary
    /// timestamp — an event with the exact same <c>OccurredAtUtc</c> as <c>prompts[0]</c>
    /// isn't guaranteed to sort a particular way relative to the boundary.
    /// </remarks>
    private static AgentConversationPromptEvent[] AttachTurnTokens(
        AgentConversationPromptEvent[] prompts,
        AgentCompletionEventForUsage[] completionEvents
    )
    {
        var result = new AgentConversationPromptEvent[prompts.Length];
        if (prompts.Length == 0)
        {
            return result;
        }

        var completionIndex = 0;
        while (
            completionIndex < completionEvents.Length
            && completionEvents[completionIndex].OccurredAtUtc < prompts[0].OccurredAtUtc
        )
        {
            // 👈 Belongs to a prompt trimmed off the front of the 500-newest window, not to
            // prompts[0] — skip without attributing.
            completionIndex++;
        }

        for (var i = 0; i < prompts.Length; i++)
        {
            var windowEnd =
                i + 1 < prompts.Length ? prompts[i + 1].OccurredAtUtc : DateTimeOffset.MaxValue;
            long turnInputTokens = 0;
            long turnOutputTokens = 0;
            var hasCompletionInWindow = false;

            while (
                completionIndex < completionEvents.Length
                && completionEvents[completionIndex].OccurredAtUtc < windowEnd
            )
            {
                var completion = completionEvents[completionIndex];
                turnInputTokens += completion.InputTokens ?? 0;
                turnOutputTokens += completion.OutputTokens ?? 0;
                hasCompletionInWindow = true;
                completionIndex++;
            }

            result[i] = hasCompletionInWindow
                ? prompts[i] with
                {
                    TurnInputTokens = turnInputTokens,
                    TurnOutputTokens = turnOutputTokens,
                }
                : prompts[i];
        }

        return result;
    }

    /// <summary>
    /// Normalized email keys that count as "mine" for the subject: their own
    /// sign-in email plus any active email aliases, all lowercased/trimmed.
    /// </summary>
    private async Task<HashSet<string>> OwnerEmailMatchKeysAsync(
        string organizationId,
        string subjectUserId,
        CancellationToken cancellationToken
    )
    {
        var ownEmail = await db
            .Users.TagWithOperationCallSite("agent_conversation.list_recent.own_email")
            .AsNoTracking()
            .Where(u => u.Id == subjectUserId && u.Email != null)
            .Select(u => u.Email!.Trim().ToLower())
            .FirstOrDefaultAsync(cancellationToken);

        var aliasEmails = await db
            .UserAliases.TagWithOperationCallSite("agent_conversation.list_recent.email_aliases")
            .AsNoTracking()
            .Where(alias =>
                alias.OrganizationId == organizationId
                && alias.UserId == subjectUserId
                && alias.Kind == UserAliasKind.Email
                && alias.DisabledAtUtc == null
            )
            .Select(alias => alias.NormalizedValue)
            .ToArrayAsync(cancellationToken);

        var keys = new HashSet<string>(aliasEmails, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(ownEmail))
        {
            keys.Add(ownEmail);
        }

        return keys;
    }
}
