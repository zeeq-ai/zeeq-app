using System.Net;
using System.Text.Json;
using FluentlyHttpClient;
using Notion.Client;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Default <see cref="IZeeqNotionClient"/> — wraps Notion HTTP calls behind Zeeq DTOs.
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
/// <para>
/// NOTE: page metadata intentionally uses raw REST through <c>FluentlyHttpClient</c> instead of
/// the SDK's <c>SearchClient.SearchAsync</c>/<c>PagesClient.RetrieveAsync</c>. The installed
/// <c>Notion.Net</c> 5.0.0 package has duplicate <c>IPageIcon</c> subtype mappings for the
/// <c>icon</c> discriminator, so otherwise-valid page responses can fail before Zeeq sees the
/// fields it actually needs. Keep this focused parser until the SDK page-object deserializer is
/// fixed upstream; the SDK is still used for markdown rendering and token identity, where its
/// response models do not hit that broken page-icon converter.
/// </para>
/// </remarks>
internal sealed class ZeeqNotionClient(INotionClient client, IFluentHttpClient pagesClient)
    : IZeeqNotionClient
{
    /// <inheritdoc/>
    /// <remarks>
    /// Pages Notion's <c>POST /v1/search</c> via <c>start_cursor</c>/<c>has_more</c> until
    /// exhausted, 100 results per page. Trashed pages are filtered out client-side — the SDK's
    /// <see cref="SearchFilter"/> has no <c>in_trash</c> parameter and the raw response parser
    /// keeps this code insulated from SDK page-icon deserialization bugs.
    /// </remarks>
    public async IAsyncEnumerable<NotionPageSummary> SearchPagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        string? cursor = null;

        do
        {
            var json = await pagesClient
                .CreateRequest("search")
                .AsPost()
                .WithBody(SearchRequestBody(cursor))
                .WithCancellationToken(ct)
                .WithSuccessStatus()
                .ReturnAsString();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            foreach (var page in root.GetProperty("results").EnumerateArray())
            {
                if (IsVisiblePage(page))
                {
                    yield return ToPageSummary(page);
                }
            }

            cursor = root.GetProperty("has_more").GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;
        } while (cursor is not null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Wraps <c>GET /v1/pages/{id}</c>. A 404 — covering both a deleted page and one the
    /// connection lost access to — is treated as "no data," not an error; any other response
    /// propagates via <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </remarks>
    public async Task<NotionPage?> GetPageAsync(string pageId, CancellationToken ct)
    {
        var response = await pagesClient
            .CreateRequest($"pages/{Uri.EscapeDataString(pageId)}")
            .WithCancellationToken(ct)
            .ReturnAsResponse();

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);

        return ToPage(document.RootElement);
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
        catch (NotionApiException ex)
            when (ex.NotionAPIErrorCode == NotionAPIErrorCode.ObjectNotFound)
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

    public void Dispose() => pagesClient.Dispose();

    private static object SearchRequestBody(string? cursor) =>
        cursor is null
            ? new { filter = new { property = "object", value = "page" }, page_size = 100 }
            : new
            {
                filter = new { property = "object", value = "page" },
                start_cursor = cursor,
                page_size = 100,
            };

    private static bool IsVisiblePage(JsonElement page) =>
        page.TryGetProperty("object", out var objectProperty)
        && objectProperty.GetString() == "page"
        && (!page.TryGetProperty("in_trash", out var inTrash) || !inTrash.GetBoolean());

    private static NotionPageSummary ToPageSummary(JsonElement page) =>
        new(
            page.GetProperty("id").GetString() ?? string.Empty,
            ExtractTitle(page),
            ToPageParent(page.GetProperty("parent")),
            page.GetProperty("last_edited_time").GetDateTimeOffset()
        );

    private static NotionPage ToPage(JsonElement page) =>
        new(
            page.GetProperty("id").GetString() ?? string.Empty,
            ExtractTitle(page),
            ToPageParent(page.GetProperty("parent"))
        );

    private static NotionPageParent ToPageParent(JsonElement parent)
    {
        var parentType = parent.GetProperty("type").GetString();

        return parentType switch
        {
            "workspace" => new NotionPageParent(IsWorkspaceRoot: true, ParentPageId: null),
            "page_id" => new NotionPageParent(
                IsWorkspaceRoot: false,
                ParentPageId: parent.GetProperty("page_id").GetString()
            ),
            // Database/data-source-parented pages have no page ancestor to walk to. Per
            // NotionPageParent's documented contract, this is a walk-termination point, distinct
            // from a true workspace root: IsWorkspaceRoot stays false with no ParentPageId.
            _ => new NotionPageParent(IsWorkspaceRoot: false, ParentPageId: null),
        };
    }

    private static string ExtractTitle(JsonElement page)
    {
        if (!page.TryGetProperty("properties", out var properties))
        {
            return "Untitled";
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (
                property.Value.TryGetProperty("type", out var type)
                && type.GetString() == "title"
                && property.Value.TryGetProperty("title", out var titleFragments)
            )
            {
                var title = string.Concat(
                    titleFragments
                        .EnumerateArray()
                        .Select(static text =>
                            text.TryGetProperty("plain_text", out var plainText)
                                ? plainText.GetString()
                                : null
                        )
                );

                return string.IsNullOrEmpty(title) ? "Untitled" : title;
            }
        }

        return "Untitled";
    }
}
