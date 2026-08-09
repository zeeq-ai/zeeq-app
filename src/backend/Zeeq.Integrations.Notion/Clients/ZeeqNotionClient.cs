using Notion.Client;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Default <see cref="IZeeqNotionClient"/> — wraps the Notion SDK's <see cref="INotionClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deviation from the spec's assumed SDK shape:</b> the spec describes the client package as
/// "<c>Notion.Net</c> v6.x, targeting Notion API version <c>2026-03-11</c>". As of this
/// implementation, the highest published NuGet version is <b>5.0.0</b> (no v6.x exists yet) and
/// its release notes state it targets <c>2025-09-03</c> as the default API version internally.
/// The package's source repository's <c>main</c> branch (ahead of the 5.0.0 tag) already defaults
/// to <c>2026-03-11</c>, matching the samples this project was built against — so
/// <see cref="ZeeqNotionClientFactory.ApiVersion"/> overrides the SDK's built-in default
/// explicitly via <see cref="ClientOptions.NotionVersion"/> rather than waiting for a future SDK
/// release. This is safe: Notion versions requests by an explicit header regardless of what the
/// client library ships as its own default, and 2026-03-11 is confirmed working directly against
/// the live API in <c>.agents/scratch/notion/notion.http</c>.
/// </para>
/// <para>
/// The spec's other Phase 3 assumptions held: <see cref="ClientOptions.HttpClient"/> exists and
/// behaves exactly as described (used as-is, not disposed by the SDK), and
/// <see cref="PageMarkdownResponse"/> natively models <c>truncated</c>/<c>unknown_block_ids</c> —
/// no manual JSON parsing was needed for those fields.
/// </para>
/// </remarks>
internal sealed class ZeeqNotionClient(INotionClient client) : IZeeqNotionClient
{
    /// <inheritdoc/>
    /// <remarks>
    /// Pages the SDK's <c>POST /v1/search</c> via <c>StartCursor</c>/<c>HasMore</c> until
    /// exhausted, 100 results per page. Trashed pages are filtered out client-side — the SDK's
    /// <see cref="SearchFilter"/> has no <c>in_trash</c> parameter, unlike the raw REST body.
    /// </remarks>
    public async IAsyncEnumerable<NotionPageSummary> SearchPagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        string? cursor = null;

        do
        {
            var response = await client.Search.SearchAsync(
                new SearchRequest
                {
                    Filter = new SearchFilter { Value = SearchObjectType.Page },
                    StartCursor = cursor,
                    PageSize = 100,
                },
                ct
            );

            foreach (var result in response.Results)
            {
                // The SDK's SearchFilter has no server-side `in_trash` parameter (unlike the
                // raw REST body captured in notion.http, which does), so trashed pages are
                // filtered out client-side instead.
                if (result is Page { InTrash: false } page)
                {
                    yield return ToPageSummary(page);
                }
            }

            cursor = response.HasMore ? response.NextCursor : null;
        } while (cursor is not null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Wraps <c>GET /v1/pages/{id}</c>. A <see cref="NotionAPIErrorCode.ObjectNotFound"/> (404) —
    /// covering both a deleted page and one the connection lost access to — is treated as "no
    /// data," not an error; any other SDK exception propagates.
    /// </remarks>
    public async Task<NotionPage?> GetPageAsync(string pageId, CancellationToken ct)
    {
        try
        {
            var page = await client.Pages.RetrieveAsync(pageId, ct);
            return new NotionPage(page.Id, ExtractTitle(page), ToPageParent(page.Parent));
        }
        catch (NotionApiException ex) when (ex.NotionAPIErrorCode == NotionAPIErrorCode.ObjectNotFound)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Wraps <c>GET /v1/pages/{id}/markdown</c> — Notion renders the block tree to Markdown
    /// server-side, so no recursive block walk or bespoke converter is needed here. Same 404
    /// handling as <see cref="GetPageAsync"/>.
    /// </remarks>
    public async Task<NotionPageMarkdown?> GetPageMarkdownAsync(string pageId, CancellationToken ct)
    {
        try
        {
            var response = await client.Pages.RetrieveAsMarkdownAsync(
                new RetrievePageAsMarkdownRequest { PageId = pageId },
                ct
            );

            return new NotionPageMarkdown(
                response.Markdown ?? string.Empty,
                response.Truncated,
                response.UnknownBlockIds?.Count() ?? 0
            );
        }
        catch (NotionApiException ex) when (ex.NotionAPIErrorCode == NotionAPIErrorCode.ObjectNotFound)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Wraps <c>GET /v1/users/me</c>. <see cref="NotionAPIErrorCode.Unauthorized"/> (bad/revoked
    /// token) and <see cref="NotionAPIErrorCode.RestrictedResource"/> (token lacks user-read
    /// capability) both mean "this token cannot be validated," which the caller treats as
    /// "invalid token" — any other SDK exception propagates.
    /// </remarks>
    public async Task<NotionConnectionIdentity?> GetConnectionIdentityAsync(CancellationToken ct)
    {
        try
        {
            var bot = await client.Users.MeAsync(ct);
            return new NotionConnectionIdentity(bot.Id, bot.Name);
        }
        catch (NotionApiException ex)
            when (ex.NotionAPIErrorCode == NotionAPIErrorCode.Unauthorized
                || ex.NotionAPIErrorCode == NotionAPIErrorCode.RestrictedResource
            )
        {
            return null;
        }
    }

    private static NotionPageSummary ToPageSummary(Page page) =>
        new(page.Id, ExtractTitle(page), ToPageParent(page.Parent), page.LastEditedTime);

    private static NotionPageParent ToPageParent(IParentOfPage parent) =>
        parent switch
        {
            WorkspaceParent => new NotionPageParent(IsWorkspaceRoot: true, ParentPageId: null),
            PageParent pageParent => new NotionPageParent(
                IsWorkspaceRoot: false,
                ParentPageId: pageParent.PageId
            ),
            // Database/data-source-parented pages have no page ancestor to walk to. Per
            // NotionPageParent's documented contract, this is a walk-termination point, distinct
            // from a true workspace root: IsWorkspaceRoot stays false with no ParentPageId.
            _ => new NotionPageParent(IsWorkspaceRoot: false, ParentPageId: null),
        };

    private static string ExtractTitle(Page page)
    {
        var titleProperty = page.Properties.Values.OfType<TitlePropertyValue>().FirstOrDefault();

        // Notion title properties are rich-text collections; a title can be split across
        // multiple fragments (e.g. mixed formatting), so every fragment must be concatenated.
        var title = string.Concat(
            titleProperty?.Title.Select(static text => text.PlainText) ?? []
        );

        return string.IsNullOrEmpty(title) ? "Untitled" : title;
    }
}
