namespace Zeeq.Integrations.Notion;

/// <summary>
/// Thin anti-corruption wrapper over the Notion SDK (<c>Notion.Net</c>), exposing only what
/// Zeeq's ingest pipeline needs.
/// </summary>
/// <remarks>
/// SDK types (<c>Notion.Client.*</c>) never leak past this interface — the same discipline
/// <c>Zeeq.Integrations.GitHub</c> uses to keep Octokit types out of platform projects. Callers
/// in <c>Zeeq.Platform.Ingest</c> depend on this and the DTOs below, not on <c>Notion.Client</c>.
/// Implementers must dispose any per-client HTTP wrappers they own; the production client uses
/// pooled handlers, so disposing the client releases wrappers without tearing down shared sockets.
/// </remarks>
public interface IZeeqNotionClient : IDisposable
{
    /// <summary>
    /// Enumerates every page the connection can see (bootstrap / full resync), paginating
    /// through the SDK's search until exhausted.
    /// </summary>
    IAsyncEnumerable<NotionPageSummary> SearchPagesAsync(CancellationToken ct);

    /// <summary>
    /// Fetches one page's metadata — title and parent reference — for path resolution.
    /// </summary>
    /// <returns><c>null</c> if the page is missing, deleted, or no longer accessible (404).</returns>
    Task<NotionPage?> GetPageAsync(string pageId, CancellationToken ct);

    /// <summary>
    /// Fetches one page rendered as Markdown by Notion.
    /// </summary>
    /// <returns><c>null</c> if the page is missing, deleted, or no longer accessible (404).</returns>
    Task<NotionPageMarkdown?> GetPageMarkdownAsync(string pageId, CancellationToken ct);

    /// <summary>
    /// Validates the token this client was created with and returns the bot/connection
    /// identity behind it.
    /// </summary>
    /// <remarks>
    /// Used at library-create time both to prove the pasted token works and to capture
    /// <c>NotionSourceConfiguration.ConnectionName</c> (spec D-6).
    /// </remarks>
    /// <returns><c>null</c> if the token is invalid or unauthorized.</returns>
    Task<NotionConnectionIdentity?> GetConnectionIdentityAsync(CancellationToken ct);
}

/// <summary>One page as returned by <c>POST /v1/search</c>.</summary>
public sealed record NotionPageSummary(
    string PageId,
    string Title,
    NotionPageParent Parent,
    DateTimeOffset LastEditedTimeUtc
);

/// <summary>One page as returned by <c>GET /v1/pages/{id}</c> — metadata only, no content.</summary>
public sealed record NotionPage(string PageId, string Title, NotionPageParent Parent);

/// <summary>
/// A page's parent reference, as needed to walk the ancestor chain during path resolution
/// (spec §3.8/§5.7). Exactly one of <see cref="ParentPageId"/> is set, or neither, when
/// <see cref="IsWorkspaceRoot"/> is <c>true</c>.
/// </summary>
/// <remarks>
/// Notion pages can also be parented by a database or data source; those are folded into
/// <see cref="ParentPageId"/> being null and <see cref="IsWorkspaceRoot"/> being false — the
/// path resolver treats an unresolvable non-page, non-workspace parent as a walk termination
/// point rather than a distinct case, since Zeeq only ingests pages (spec D-5).
/// </remarks>
public sealed record NotionPageParent(bool IsWorkspaceRoot, string? ParentPageId);

/// <summary>The response from <c>GET /v1/pages/{id}/markdown</c>.</summary>
/// <remarks>
/// <see cref="Truncated"/> and <see cref="UnknownBlockCount"/> are surfaced but never treated
/// as failures by the caller — per spec D-2, truncation is almost always permission-driven and
/// unrecoverable (the unknown blocks 404 on re-fetch), so ingesting what is visible is correct.
/// </remarks>
public sealed record NotionPageMarkdown(string Markdown, bool Truncated, int UnknownBlockCount);

/// <summary>The bot/connection identity behind a validated Notion access token.</summary>
/// <remarks>
/// Maps from the SDK's <c>Users.MeAsync()</c> (<c>GET /v1/users/me</c>). For an internal
/// (workspace-owned) integration — the only auth method v1 supports (spec §3.1, Access Token) —
/// Notion names the bot user after the integration itself, so <see cref="Name"/> is exactly the
/// connection name shown in Notion's UI (spec D-6): the fixture responses Notion's own SDK test
/// suite ships (<c>Test/Notion.UnitTests/data/users/MeResponse.json</c>) confirm the bot's
/// <c>name</c> is the only human-readable identity field present — there is no separate
/// <c>workspace_name</c>/<c>workspace_id</c> on this response for either the internal or the
/// OAuth-level bot shape, which is why a workspace name/id are not exposed on this DTO at all
/// (kept off entirely rather than modeled as permanently-null fields).
/// This has NOT been verified against a live Notion access token — if a live token's bot name
/// turns out not to match the connection name shown in Notion's UI, the documented fallback is
/// to let the user type the connection name in Zeeq's create form instead of trusting this value.
/// </remarks>
public sealed record NotionConnectionIdentity(string BotUserId, string Name);
