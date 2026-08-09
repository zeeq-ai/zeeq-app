using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/NotionIngestRunnerTests/*"
/// </summary>
public sealed class NotionIngestRunnerTests
{
    private readonly FakeLibraryDocumentStore _libraries = new();
    private readonly IExternalPendingContentSyncStore _pending =
        Substitute.For<IExternalPendingContentSyncStore>();
    private readonly FakeDocsIngestRunStore _runs = new();
    private readonly FakeNotionClient _client = new();

    public NotionIngestRunnerTests() =>
        _pending
            .IsClaimActiveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

    [Test]
    public async Task IncrementalScope_DoesNotRunDeletionSweep()
    {
        _libraries.Documents.Add(Document("old-page", "/old", "old-run"));
        ArrangeIncremental("new-page");
        _client.Pages["new-page"] = Page("new-page", "New");
        _client.Markdown["new-page"] = new NotionPageMarkdown("# New", false, 0);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Incremental), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert
            .That(_libraries.Documents.Select(row => row.SourceExternalId!))
            .IsEquivalentTo(["old-page", "new-page"]);
    }

    [Test]
    public async Task FullScope_CleanPass_SweepsUnstampedDocuments()
    {
        _libraries.Documents.Add(Document("old-page", "/old", "old-run"));
        _client.SearchResults.Add(Summary("new-page", "New"));
        _client.Markdown["new-page"] = new NotionPageMarkdown("# New", false, 0);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesDeleted).IsEqualTo(1);
        await Assert.That(_libraries.Documents).HasSingleItem();
        await Assert.That(_libraries.Documents[0].SourceExternalId).IsEqualTo("new-page");
    }

    [Test]
    public async Task FullScope_WithPageFailure_SkipsSweep()
    {
        _libraries.Documents.Add(Document("old-page", "/old", "old-run"));
        _client.SearchResults.Add(Summary("bad-page", "Bad"));
        _client.MarkdownErrors.Add("bad-page");

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesFailed).IsEqualTo(1);
        await Assert.That(_libraries.Documents).HasSingleItem();
        await Assert.That(_libraries.Documents[0].SourceExternalId).IsEqualTo("old-page");
    }

    [Test]
    public async Task FullScope_PageFailure_IsolatesAndContinues()
    {
        _client.SearchResults.Add(Summary("bad-page", "Bad"));
        _client.SearchResults.Add(Summary("good-page", "Good"));
        _client.MarkdownErrors.Add("bad-page");
        _client.Markdown["good-page"] = new NotionPageMarkdown("# Good", false, 0);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesFailed).IsEqualTo(1);
        await Assert.That(run.FilesAdded).IsEqualTo(1);
        await Assert.That(_libraries.Documents[0].SourceExternalId).IsEqualTo("good-page");
    }

    [Test]
    public async Task IncrementalScope_FilteredPage_NeverFetchesMarkdownAndClearsClaim()
    {
        ArrangeIncremental("page-1");
        _client.Pages["page-1"] = Page("page-1", "Blocked");

        var run = await Runner()
            .RunAsync(
                Job(ExternalSyncScope.Incremental, new EffectiveFilter(["allowed/**"], [])),
                _client,
                default
            );

        await Assert.That(run.FilesSkipped).IsEqualTo(1);
        await Assert.That(_client.MarkdownCalls).IsEmpty();
        await _pending
            .Received(1)
            .ClearAsync("org-1", "library-1", "page-1", "run-1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IncrementalScope_AbsentPage_DeletesDocumentAndClearsClaim()
    {
        _libraries.Documents.Add(Document("page-1", "/old", "old-run"));
        ArrangeIncremental("page-1");

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Incremental), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesDeleted).IsEqualTo(1);
        await Assert.That(_libraries.Documents).IsEmpty();
        await _pending
            .Received(1)
            .ClearAsync("org-1", "library-1", "page-1", "run-1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FullScope_MarkdownUnavailable_FailsPageInsteadOfDeletingAndSkipsSweep()
    {
        _libraries.Documents.Add(Document("old-page", "/old", "old-run"));
        _client.SearchResults.Add(Summary("old-page", "Old"));
        // No Markdown entry for "old-page": simulates a transient read failure on a page that
        // full search just reported as visible, distinct from MarkdownErrors' thrown exception.

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Partial);
        await Assert.That(run.FilesFailed).IsEqualTo(1);
        await Assert.That(run.FilesDeleted).IsEqualTo(0);
        await Assert.That(_libraries.Documents).HasSingleItem();
        await Assert.That(_libraries.Documents[0].SourceExternalId).IsEqualTo("old-page");
    }

    [Test]
    public async Task IncrementalScope_ClaimInvalidatedMidFetch_SkipsWriteInsteadOfResurrectingPage()
    {
        ArrangeIncremental("page-1");
        _client.Pages["page-1"] = Page("page-1", "New");
        _client.Markdown["page-1"] = new NotionPageMarkdown("# New", false, 0);
        _pending
            .IsClaimActiveAsync(
                "org-1",
                "library-1",
                "page-1",
                "run-1",
                Arg.Any<CancellationToken>()
            )
            .Returns(false);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Incremental), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(_libraries.Documents).IsEmpty();
    }

    [Test]
    public async Task FullScope_TruncatedMarkdown_IngestsWithoutPartialStatus()
    {
        _client.SearchResults.Add(Summary("page-1", "Visible"));
        _client.Markdown["page-1"] = new NotionPageMarkdown("# Visible", true, 3);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert.That(run.FilesAdded).IsEqualTo(1);
        await Assert.That(run.FilesFailed).IsEqualTo(0);
    }

    [Test]
    public async Task FullScope_DuplicateBreadcrumbs_UsesStablePageIdSuffix()
    {
        _client.SearchResults.Add(Summary("aaaaaaaa-1111", "Roadmap"));
        _client.SearchResults.Add(Summary("bbbbbbbb-2222", "Roadmap"));
        _client.Markdown["aaaaaaaa-1111"] = new NotionPageMarkdown("# First", false, 0);
        _client.Markdown["bbbbbbbb-2222"] = new NotionPageMarkdown("# Second", false, 0);

        var run = await Runner().RunAsync(Job(ExternalSyncScope.Full), _client, default);

        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Succeeded);
        await Assert
            .That(_libraries.Documents.Select(row => row.Path))
            .IsEquivalentTo(["/roadmap", "/roadmap--bbbbbbbb"]);
    }

    private NotionIngestRunner Runner() =>
        new(
            _libraries,
            _pending,
            _runs,
            new IngestSettings(),
            NullLogger<NotionIngestRunner>.Instance
        );

    private void ArrangeIncremental(params string[] pageIds) =>
        _pending
            .ClaimAsync(
                "org-1",
                "library-1",
                "run-1",
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult<IReadOnlyList<ExternalPendingContentSync>>([
                    .. pageIds.Select(id => new ExternalPendingContentSync(
                        id,
                        "page.content_updated"
                    )),
                ])
            );

    private static NotionIngestJob Job(ExternalSyncScope scope, EffectiveFilter? filter = null) =>
        new()
        {
            RunId = "run-1",
            RunCreatedAtUtc = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            OrganizationId = "org-1",
            LibraryId = "library-1",
            Scope = scope,
            Trigger = IngestTriggerReason.Scheduled,
            Filter = filter ?? EffectiveFilter.Empty,
            SourceReference = "notion://library/library-1",
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static NotionPage Page(string id, string title) =>
        new(id, title, new NotionPageParent(true, null));

    private static NotionPageSummary Summary(string id, string title) =>
        new(id, title, new NotionPageParent(true, null), DateTimeOffset.UtcNow);

    private static LibraryDocument Document(string pageId, string path, string runId) =>
        new()
        {
            Id = $"doc-{pageId}",
            OrganizationId = "org-1",
            LibraryId = "library-1",
            Path = path,
            Title = pageId,
            TitleNormalized = pageId,
            Content = pageId,
            ContentHash = pageId,
            SourceExternalId = pageId,
            SyncRunId = runId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class FakeNotionClient : IZeeqNotionClient
    {
        internal List<NotionPageSummary> SearchResults { get; } = [];
        internal Dictionary<string, NotionPage> Pages { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, NotionPageMarkdown> Markdown { get; } =
            new(StringComparer.Ordinal);
        internal HashSet<string> MarkdownErrors { get; } = new(StringComparer.Ordinal);
        internal List<string> MarkdownCalls { get; } = [];

        public async IAsyncEnumerable<NotionPageSummary> SearchPagesAsync(
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            foreach (var page in SearchResults)
            {
                ct.ThrowIfCancellationRequested();
                yield return page;
            }

            await Task.CompletedTask;
        }

        public Task<NotionPage?> GetPageAsync(string pageId, CancellationToken ct) =>
            Task.FromResult(Pages.GetValueOrDefault(pageId));

        public Task<NotionPageMarkdown?> GetPageMarkdownAsync(string pageId, CancellationToken ct)
        {
            MarkdownCalls.Add(pageId);
            return MarkdownErrors.Contains(pageId)
                ? Task.FromException<NotionPageMarkdown?>(
                    new InvalidOperationException("markdown failed")
                )
                : Task.FromResult<NotionPageMarkdown?>(Markdown.GetValueOrDefault(pageId));
        }

        public Task<NotionConnectionIdentity?> GetConnectionIdentityAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
