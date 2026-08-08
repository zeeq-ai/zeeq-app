using System.Net;
using FluentlyHttpClient;
using Microsoft.Extensions.DependencyInjection;
using Notion.Client;

namespace Zeeq.Integrations.Notion.Tests;

/// <summary>
/// Tests for <see cref="ZeeqNotionClient"/>'s SDK wrapping behavior.
///
/// dotnet run --project src/backend/Zeeq.Integrations.Notion.Tests --output detailed --disable-logo --treenode-filter "/*/*/ZeeqNotionClientTests/*"
/// </summary>
public sealed class ZeeqNotionClientTests
{
    [Test]
    public async Task NotionClient_SearchPages_FollowsNextCursor()
    {
        // First page of results reports has_more/next_cursor; the client must issue a
        // second request carrying that cursor and keep yielding until has_more is false.
        var stub = new TwoPageSearchHandler();
        using var client = CreateClient(stub);

        var pages = new List<NotionPageSummary>();
        await foreach (var page in client.SearchPagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That(pages[0].PageId).IsEqualTo("page-1");
        await Assert.That(pages[1].PageId).IsEqualTo("page-2");
        await Assert.That(stub.RequestCount).IsEqualTo(2);
        await Assert.That(stub.FirstRequestSentNullCursor).IsFalse();
        await Assert.That(stub.SecondRequestCursor).IsEqualTo("cursor-abc");
    }

    [Test]
    public async Task NotionClient_SearchPages_ExcludesTrashedPages()
    {
        var stub = new SinglePageWithTrashHandler();
        using var client = CreateClient(stub);

        var pages = new List<NotionPageSummary>();
        await foreach (var page in client.SearchPagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        // Only the non-trashed page survives — the SDK's SearchFilter has no server-side
        // in_trash parameter, so this filtering must happen client-side.
        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].PageId).IsEqualTo("page-live");
    }

    [Test]
    public async Task NotionClient_SearchPages_ConcatenatesMultiFragmentTitle()
    {
        // A Notion title can be split across multiple rich-text fragments (e.g. mixed
        // formatting within one title). All fragments must be preserved, not just the first.
        var stub = new MultiFragmentTitleHandler();
        using var client = CreateClient(stub);

        var pages = new List<NotionPageSummary>();
        await foreach (var page in client.SearchPagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        await Assert.That(pages.Single().Title).IsEqualTo("2026 Q4 Engineering Initiatives");
    }

    [Test]
    public async Task NotionClient_SearchPages_DatabaseParentIsNotWorkspaceRoot()
    {
        // A database/data-source-parented page has no page ancestor to walk to, but it is NOT
        // a true workspace root — the two must stay distinguishable per NotionPageParent's
        // documented contract.
        var stub = new DatabaseParentHandler();
        using var client = CreateClient(stub);

        var pages = new List<NotionPageSummary>();
        await foreach (var page in client.SearchPagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        var parent = pages.Single().Parent;
        await Assert.That(parent.IsWorkspaceRoot).IsFalse();
        await Assert.That(parent.ParentPageId).IsNull();
    }

    [Test]
    public async Task NotionClient_SearchPages_IgnoresPageIconShape()
    {
        // Notion.Net 5.0.0 has duplicate JsonSubTypes mappings for IPageIcon's "icon"
        // discriminator. Zeeq only needs title/parent/last-edited for search, so the raw REST
        // parser must tolerate icon metadata that would otherwise break SDK page deserialization.
        var stub = new IconPageHandler();
        using var client = CreateClient(stub);

        var pages = new List<NotionPageSummary>();
        await foreach (var page in client.SearchPagesAsync(CancellationToken.None))
        {
            pages.Add(page);
        }

        await Assert.That(pages.Single().PageId).IsEqualTo("page-with-icon");
    }

    [Test]
    public async Task NotionClient_GetPage_NotFound_ReturnsNull()
    {
        var stub = new NotFoundHandler();
        using var client = CreateClient(stub);

        var page = await client.GetPageAsync("missing-page", CancellationToken.None);

        await Assert.That(page).IsNull();
    }

    private static ZeeqNotionClient CreateClient(HttpMessageHandler handler)
    {
        var services = new ServiceCollection().AddFluentlyHttpClient().BuildServiceProvider();
        var fluentClient = services
            .GetRequiredService<IFluentHttpClientFactory>()
            .CreateBuilder($"notion-test-{Guid.NewGuid():N}")
            .WithBaseUrl("https://api.notion.com/v1/")
            .WithMessageHandler(handler)
            .Build(skipAutoRegister: true);
        var notionClient = NotionClientFactory.Create(
            new ClientOptions
            {
                AuthToken = "test-token",
                HttpClient = new HttpClient(new UnusedHandler())
                {
                    BaseAddress = new Uri("https://api.notion.com/"),
                },
            }
        );

        return new ZeeqNotionClient(notionClient, fluentClient);
    }

    private sealed class MultiFragmentTitleHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            const string json = """
                {
                  "object": "list",
                  "results": [
                    {
                      "object": "page",
                      "id": "page-multi-title",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "workspace", "workspace": true },
                      "in_trash": false,
                      "properties": {
                        "title": {
                          "id": "title",
                          "type": "title",
                          "title": [
                            { "type": "text", "plain_text": "2026 Q4 ", "text": { "content": "2026 Q4 " } },
                            { "type": "text", "plain_text": "Engineering ", "text": { "content": "Engineering " } },
                            { "type": "text", "plain_text": "Initiatives", "text": { "content": "Initiatives" } }
                          ]
                        }
                      }
                    }
                  ],
                  "next_cursor": null,
                  "has_more": false
                }
                """;

            return Task.FromResult(JsonResponse(json));
        }
    }

    private sealed class DatabaseParentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            const string json = """
                {
                  "object": "list",
                  "results": [
                    {
                      "object": "page",
                      "id": "page-in-database",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "data_source_id", "data_source_id": "some-data-source-id" },
                      "in_trash": false,
                      "properties": { "title": { "id": "title", "type": "title", "title": [] } }
                    }
                  ],
                  "next_cursor": null,
                  "has_more": false
                }
                """;

            return Task.FromResult(JsonResponse(json));
        }
    }

    private sealed class IconPageHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            const string json = """
                {
                  "object": "list",
                  "results": [
                    {
                      "object": "page",
                      "id": "page-with-icon",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "workspace", "workspace": true },
                      "icon": { "type": "icon", "icon": { "type": "native", "native": "home" } },
                      "in_trash": false,
                      "properties": { "title": { "id": "title", "type": "title", "title": [] } }
                    }
                  ],
                  "next_cursor": null,
                  "has_more": false
                }
                """;

            return Task.FromResult(JsonResponse(json));
        }
    }

    private sealed class TwoPageSearchHandler : DelegatingHandler
    {
        public int RequestCount { get; private set; }
        public bool FirstRequestSentNullCursor { get; private set; }
        public string? SecondRequestCursor { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;

            if (RequestCount == 1)
            {
                var firstBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                FirstRequestSentNullCursor = firstBody.Contains("start_cursor");

                return JsonResponse(
                    SearchResponseJson(pageId: "page-1", hasMore: true, nextCursor: "cursor-abc")
                );
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            SecondRequestCursor = body.Contains("cursor-abc") ? "cursor-abc" : null;

            return JsonResponse(
                SearchResponseJson(pageId: "page-2", hasMore: false, nextCursor: null)
            );
        }

        private static string SearchResponseJson(string pageId, bool hasMore, string? nextCursor) =>
            $$"""
                {
                  "object": "list",
                  "results": [
                    {
                      "object": "page",
                      "id": "{{pageId}}",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "workspace", "workspace": true },
                      "in_trash": false,
                      "is_archived": false,
                      "properties": {
                        "title": {
                          "id": "title",
                          "type": "title",
                          "title": [ { "type": "text", "plain_text": "Test Page", "text": { "content": "Test Page" } } ]
                        }
                      }
                    }
                  ],
                  "next_cursor": {{(nextCursor is null ? "null" : $"\"{nextCursor}\"")}},
                  "has_more": {{(hasMore ? "true" : "false")}}
                }
                """;
    }

    private sealed class SinglePageWithTrashHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            const string json = """
                {
                  "object": "list",
                  "results": [
                    {
                      "object": "page",
                      "id": "page-live",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "workspace", "workspace": true },
                      "in_trash": false,
                      "properties": { "title": { "id": "title", "type": "title", "title": [] } }
                    },
                    {
                      "object": "page",
                      "id": "page-trashed",
                      "created_time": "2026-08-08T15:52:00.000Z",
                      "last_edited_time": "2026-08-08T15:52:00.000Z",
                      "parent": { "type": "workspace", "workspace": true },
                      "in_trash": true,
                      "properties": { "title": { "id": "title", "type": "title", "title": [] } }
                    }
                  ],
                  "next_cursor": null,
                  "has_more": false
                }
                """;

            return Task.FromResult(JsonResponse(json));
        }
    }

    private sealed class NotFoundHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class UnusedHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("The SDK client should not be used by this test.");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
}
