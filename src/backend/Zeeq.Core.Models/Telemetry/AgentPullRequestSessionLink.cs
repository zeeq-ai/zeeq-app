using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Zeeq.Core.Models;

/// <summary>
/// Bidirectional link between code reviews and agent sessions.
/// </summary>
/// <remarks>
/// Sessions are linked either by automated marker parsing (<c>PrMarker</c> origin)
/// or user curation via review request (<c>UserCurated</c> origin).
/// </remarks>
[Table("agent_pull_request_session_links")]
public sealed class AgentPullRequestSessionLink
{
    /// <summary>UUID v7 identifier.</summary>
    [Key]
    public required string Id { get; set; }

    /// <summary>Owning organization; distribution key.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>FK to <c>code_review_pull_request_records.Id</c>.</summary>
    public required string PullRequestRecordId { get; set; }

    /// <summary>FK to <c>agent_conversations.id</c>.</summary>
    public required string ConversationId { get; set; }

    /// <summary>How the link was created.</summary>
    public AgentSessionLinkOrigin LinkOrigin { get; set; }

    /// <summary>When the link was created.</summary>
    public required DateTimeOffset LinkedAtUtc { get; set; }

    /// <summary>User who created the link (null if automated).</summary>
    public string? LinkedByUserId { get; set; }

    /// <summary>True when the link comes from an unresolved marker awaiting manual confirmation.</summary>
    public bool IsPending { get; set; }
}

/// <summary>How a PR-session link was created.</summary>
public enum AgentSessionLinkOrigin
{
    /// <summary>Automated: <c>Zeeq-Session: &lt;harness&gt;/&lt;session-id&gt;</c> in PR text.</summary>
    PrMarker = 1,

    /// <summary>User explicitly linked via review request UI or comment.</summary>
    UserCurated = 2,

    /// <summary>Future: GitHub webhook analysis (e.g., commit message parsing).</summary>
    WebhookCurated = 3,
}

/// <summary>
/// Composite key for looking up a conversation by organization and conversation ID.
/// </summary>
public readonly record struct AgentConversationKey(string OrganizationId, string ConversationId);
