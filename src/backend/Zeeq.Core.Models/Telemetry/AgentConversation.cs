using System.Text.Json;

namespace Zeeq.Core.Models;

/// <summary>
/// Durable conversation-level record derived from agent harness telemetry.
/// </summary>
/// <remarks>
/// The ID is the telemetry <c>conversation.id</c> / <c>session.id</c> /
/// <c>chat_session_id</c> — unique within an organization. Merge behavior
/// intentionally lives on this entity so both Postgres and future providers need
/// the same rules without event-level idempotency.
/// </remarks>
public sealed class AgentConversation
{
    /// <summary>Telemetry conversation ID.</summary>
    public required string Id { get; init; }

    /// <summary>Owning organization; distribution key.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>
    /// Harness family — <c>claude-code</c>, <c>codex</c>, <c>copilot-chat</c>, or future/unknown.
    /// Set once by each adapter's <c>Adapt()</c>.
    /// </summary>
    public required string Harness { get; set; }

    /// <summary>Codex <c>originator</c> or Claude <c>terminal.type</c>.</summary>
    public string? HarnessVariant { get; set; }

    /// <summary>Codex <c>app.version</c>.</summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Canonical repository identity in <c>owner/repo</c> form, normalized from harness remote
    /// metadata before persistence.
    /// </summary>
    public string? RepoRemoteUrl { get; set; }

    /// <summary>Copilot <c>copilot_chat.repo.head.branch</c>.</summary>
    public string? HeadBranch { get; set; }

    /// <summary>Copilot <c>copilot_chat.repo.head.sha</c>.</summary>
    public string? HeadSha { get; set; }

    /// <summary>Earliest accepted event timestamp.</summary>
    public required DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>Latest accepted event timestamp.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Total input tokens across all completions.</summary>
    public int TotalInputTokens { get; set; }

    /// <summary>Total output tokens across all completions.</summary>
    public int TotalOutputTokens { get; set; }

    /// <summary>Total estimated or reported cost in USD.</summary>
    public decimal? TotalCostUsd { get; set; }

    /// <summary>Harness-reported owner email.</summary>
    public string? OwnerEmail { get; set; }

    /// <summary>Normalized Zeeq user identity for authorization.</summary>
    public string? CreatedById { get; set; }

    /// <summary>Whether raw telemetry identity was trusted for Zeeq ownership.</summary>
    public AgentConversationOwnershipStatus OwnershipStatus { get; set; } =
        AgentConversationOwnershipStatus.Unmatched;

    /// <summary>Soft-delete metadata; non-null means user-facing reads should hide the conversation.</summary>
    public JsonDocument? SoftDeleteMetadata { get; set; }

    /// <summary>
    /// Folds a partial conversation observation into this durable conversation.
    /// </summary>
    /// <remarks>
    /// Conservative rules: timestamps widen, empty fields fill, ownership only
    /// upgrades. This prevents later partial events from overwriting more trusted
    /// values while allowing completion/tool records to create a minimal
    /// conversation before a prompt arrives.
    /// </remarks>
    public void MergeFrom(AgentConversation other)
    {
        if (StartedAtUtc > other.StartedAtUtc)
        {
            StartedAtUtc = other.StartedAtUtc;
        }

        if (
            CompletedAtUtc is null
            || (other.CompletedAtUtc is not null && CompletedAtUtc < other.CompletedAtUtc)
        )
        {
            CompletedAtUtc = other.CompletedAtUtc;
        }

        HarnessVariant = PreferExisting(HarnessVariant, other.HarnessVariant);
        AppVersion = PreferExisting(AppVersion, other.AppVersion);
        RepoRemoteUrl = PreferExisting(RepoRemoteUrl, other.RepoRemoteUrl);
        HeadBranch = PreferExisting(HeadBranch, other.HeadBranch);
        HeadSha = PreferExisting(HeadSha, other.HeadSha);
        OwnerEmail = PreferExisting(OwnerEmail, other.OwnerEmail);

        if (ShouldUpgradeOwnership(other))
        {
            OwnershipStatus = other.OwnershipStatus;
            CreatedById = other.CreatedById;
        }
    }

    private bool ShouldUpgradeOwnership(AgentConversation other) =>
        other.OwnershipStatus > OwnershipStatus && !string.IsNullOrWhiteSpace(other.CreatedById);

    private static string? PreferExisting(string? existing, string? incoming) =>
        string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming
            : existing;
}
