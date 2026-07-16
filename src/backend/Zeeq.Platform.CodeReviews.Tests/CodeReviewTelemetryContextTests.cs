using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests <see cref="CodeReviewTelemetryContext.Snapshot" /> aggregation: document/snippet grouping,
/// hit counts, relevance signals, the search→read funnel, dedup, caps, and concurrency safety.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewTelemetryContextTests/*"
/// </summary>
public sealed class CodeReviewTelemetryContextTests
{
    [Test]
    public async Task Snapshot_AggregatesSnippetsUnderDocumentByPath()
    {
        var context = new CodeReviewTelemetryContext();

        // Doc A: two hits on the same section heading (one snippet, hc=2) + one code snippet.
        Record(
            context,
            "Security",
            Section("/a.md", "A > Intro", "doc_a", "sn_a1", query: "logging")
        );
        Record(
            context,
            "Performance",
            Section("/a.md", "A > Intro", "doc_a", "sn_a1", query: "otel")
        );
        Record(
            context,
            "Performance",
            Code("/a.md", "A > Sample", "doc_a", "sn_a2", query: "batching")
        );
        // Doc B: single section hit.
        Record(context, "Structural", Section("/b.md", "B > Rules", "doc_b", "sn_b1"));

        var snapshot = context.Snapshot();

        await Assert.That(snapshot.Documents.Count).IsEqualTo(2);
        await Assert.That(snapshot.Summary.DocumentCount).IsEqualTo(2);
        await Assert.That(snapshot.Summary.SnippetCount).IsEqualTo(3);
        await Assert.That(snapshot.Summary.SourceHitCount).IsEqualTo(4);

        // Documents ordered by hit count desc: A (3) before B (1).
        var docA = snapshot.Documents[0];
        await Assert.That(docA.Path).IsEqualTo("/a.md");
        await Assert.That(docA.DocumentId).IsEqualTo("doc_a");
        await Assert.That(docA.HitCount).IsEqualTo(3);
        await Assert.That(docA.Snippets.Count).IsEqualTo(2);
        await Assert.That(docA.Facets).IsEquivalentTo(["Performance", "Security"]);

        // The repeated section groups into one snippet with hc=2 carrying both facets + queries.
        var intro = docA.Snippets.Single(snippet => snippet.Heading == "A > Intro");
        await Assert.That(intro.SnippetId).IsEqualTo("sn_a1");
        await Assert.That(intro.Kind).IsEqualTo("Section");
        await Assert.That(intro.HitCount).IsEqualTo(2);
        await Assert.That(intro.Facets).IsEquivalentTo(["Performance", "Security"]);
        await Assert.That(intro.Queries).IsEquivalentTo(["logging", "otel"]);

        var sample = docA.Snippets.Single(snippet => snippet.Heading == "A > Sample");
        await Assert.That(sample.Kind).IsEqualTo("CodeSample");
        await Assert.That(sample.Language).IsEqualTo("csharp");

        await Assert.That(snapshot.Documents[1].Path).IsEqualTo("/b.md");
    }

    [Test]
    public async Task Snapshot_ComputesBestRankScoreAndReadAfterSearch()
    {
        var context = new CodeReviewTelemetryContext();

        Record(
            context,
            "Security",
            Section("/a.md", "A > X", "doc_a", "sn_a", rank: 4, score: 0.01, im: false)
        );
        Record(
            context,
            "Security",
            Section("/a.md", "A > X", "doc_a", "sn_a", rank: 2, score: 0.03, im: true)
        );
        // The same document is later read directly (no rank).
        Record(context, "Security", Read("/a.md", "doc_a"));

        var snapshot = context.Snapshot();
        var doc = snapshot.Documents.Single();

        await Assert.That(doc.BestRank).IsEqualTo(2);
        await Assert.That(doc.BestScore).IsEqualTo(0.03);
        await Assert.That(doc.ReadAfterSearch).IsTrue();
        await Assert.That(doc.Usages).IsEquivalentTo(["Read", "Searched"]);

        var snippet = doc.Snippets.Single();
        await Assert.That(snippet.BestRank).IsEqualTo(2);
        await Assert.That(snippet.BestScore).IsEqualTo(0.03);
        await Assert.That(snippet.IdentifierMatch).IsTrue();
    }

    [Test]
    public async Task Snapshot_CountsDocLevelAndReadHitsAtDocumentLevelOnly()
    {
        var context = new CodeReviewTelemetryContext();

        Record(context, "Structural", DocSearch("/a.md", "doc_a", query: "endpoints", rank: 1));
        Record(context, "Structural", Read("/a.md", "doc_a"));

        var snapshot = context.Snapshot();
        var doc = snapshot.Documents.Single();

        await Assert.That(doc.Snippets).IsEmpty();
        await Assert.That(doc.HitCount).IsEqualTo(2);
        await Assert.That(doc.Usages).IsEquivalentTo(["Read", "Searched"]);
        await Assert.That(doc.ReadAfterSearch).IsTrue();
        await Assert.That(doc.Queries).IsEquivalentTo(["endpoints"]);
        await Assert.That(doc.BestRank).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_CapsDocumentsAndSnippets()
    {
        var context = new CodeReviewTelemetryContext();

        // 60 documents (cap 50), each with 30 distinct-heading snippets (cap 25 per document).
        for (var d = 0; d < 60; d++)
        {
            for (var s = 0; s < 30; s++)
            {
                Record(
                    context,
                    "Security",
                    Section($"/doc_{d:D2}.md", $"H{s:D2}", $"doc_{d}", $"sn_{d}_{s}", rank: 1)
                );
            }
        }

        var snapshot = context.Snapshot();

        await Assert.That(snapshot.Documents.Count).IsEqualTo(50);
        await Assert
            .That(snapshot.Documents.All(document => document.Snippets.Count <= 25))
            .IsTrue();
        await Assert.That(snapshot.Documents[0].Snippets.Count).IsEqualTo(25);
    }

    [Test]
    public async Task Snapshot_CapsFacetsAndQueriesPerNode()
    {
        var context = new CodeReviewTelemetryContext();

        // One snippet hit from 20 distinct facets, each with its own query (both cap at 10).
        for (var i = 0; i < 20; i++)
        {
            Record(
                context,
                $"Facet{i:D2}",
                Section("/capped.md", "H", "doc_capped", "sn_capped", query: $"q{i:D2}", rank: 1)
            );
        }

        var doc = context.Snapshot().Documents.Single();
        var snippet = doc.Snippets.Single();

        await Assert.That(doc.Facets.Count).IsEqualTo(10);
        await Assert.That(snippet.Facets.Count).IsEqualTo(10);
        await Assert.That(snippet.Queries.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Snapshot_CapsMissedQueries()
    {
        var context = new CodeReviewTelemetryContext();

        for (var i = 0; i < 40; i++)
        {
            RecordMiss(context, "Security", "search_sections", $"miss{i:D2}");
        }

        await Assert.That(context.Snapshot().MissedQueries.Count).IsEqualTo(25);
    }

    [Test]
    public async Task Snapshot_MissedQueriesDedupedByToolAndQuery_WithFacets()
    {
        var context = new CodeReviewTelemetryContext();

        RecordMiss(context, "Security", "search_sections", "aspire lock");
        RecordMiss(context, "Performance", "search_sections", "aspire lock");
        RecordMiss(context, "Security", "search_code_snippets", "aspire lock");

        var snapshot = context.Snapshot();

        // Distinct by (tool, query): the two search_sections misses collapse into one.
        await Assert.That(snapshot.MissedQueries.Count).IsEqualTo(2);
        await Assert.That(snapshot.Summary.MissedQueryCount).IsEqualTo(2);

        var sectionMiss = snapshot.MissedQueries.Single(miss => miss.Tool == "search_sections");
        await Assert.That(sectionMiss.Query).IsEqualTo("aspire lock");
        await Assert.That(sectionMiss.Facets).IsEquivalentTo(["Performance", "Security"]);
    }

    [Test]
    public async Task Snapshot_ToolUsageAggregatesCallsSuccessFailure()
    {
        var context = new CodeReviewTelemetryContext();

        context.RecordToolInvocation("search_sections", succeeded: true);
        context.RecordToolInvocation("search_sections", succeeded: true);
        context.RecordToolInvocation("search_sections", succeeded: false);
        context.RecordToolInvocation("read_document_by_path", succeeded: true);

        var snapshot = context.Snapshot();

        await Assert.That(snapshot.Summary.ToolCallCount).IsEqualTo(4);

        // Ordered by calls desc: search_sections (3) first.
        var sections = snapshot.ToolUsage[0];
        await Assert.That(sections.Tool).IsEqualTo("search_sections");
        await Assert.That(sections.Calls).IsEqualTo(3);
        await Assert.That(sections.Succeeded).IsEqualTo(2);
        await Assert.That(sections.Failed).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_WhenNoHitsNorMisses_ReturnsEmpty()
    {
        var context = new CodeReviewTelemetryContext();

        var snapshot = context.Snapshot();

        await Assert.That(snapshot.IsEmpty).IsTrue();
        await Assert.That(snapshot).IsSameReferenceAs(CodeReviewSourceTelemetry.Empty);
    }

    [Test]
    public async Task RecordSource_FromParallelTasks_DoesNotLoseRows()
    {
        var context = new CodeReviewTelemetryContext();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 500),
            (i, _) =>
            {
                Record(
                    context,
                    "Security",
                    Section($"/doc_{i % 10}.md", $"H{i}", $"doc_{i % 10}", $"sn_{i}", rank: 1)
                );

                return ValueTask.CompletedTask;
            }
        );

        var snapshot = context.Snapshot();

        // 500 hits across 10 documents; ConcurrentBag must not drop any.
        await Assert.That(snapshot.Summary.SourceHitCount).IsEqualTo(500);
        await Assert.That(snapshot.Documents.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Snapshot_MissedQueries_MoreFrequentSurvivesCap()
    {
        var context = new CodeReviewTelemetryContext();

        // 25 distinct one-off misses (fills the cap) plus one missed 5 times.
        for (var i = 0; i < 25; i++)
        {
            RecordMiss(context, "Security", "search_sections", $"once{i:D2}");
        }

        for (var n = 0; n < 5; n++)
        {
            RecordMiss(context, "Security", "search_sections", "frequent gap");
        }

        var snapshot = context.Snapshot();

        // 26 distinct → capped to 25; the frequently-missed gap must survive over a one-off.
        await Assert.That(snapshot.MissedQueries.Count).IsEqualTo(25);
        await Assert
            .That(snapshot.MissedQueries.Any(miss => miss.Query == "frequent gap"))
            .IsTrue();
    }

    [Test]
    public async Task BeginToolInvocationScope_OutOfOrderDispose_DoesNotClobberNestedFacet()
    {
        var context = new CodeReviewTelemetryContext();

        var outer = context.BeginToolInvocationScope("Security");
        var inner = context.BeginToolInvocationScope("Performance");

        // Dispose the OUTER scope first (out of order). The identity guard must leave the inner
        // facet active so a source recorded now is still attributed to Performance.
        outer.Dispose();
        context.RecordSource(Section("/a.md", "A > X", "doc_a", "sn_a"));
        inner.Dispose();

        var document = context.Snapshot().Documents.Single();
        await Assert.That(document.Facets).IsEquivalentTo(["Performance"]);
    }

    private static void Record(
        CodeReviewTelemetryContext context,
        string facet,
        ToolKnowledgeSource source
    )
    {
        using (context.BeginToolInvocationScope(facet))
        {
            context.RecordSource(source);
        }
    }

    private static void RecordMiss(
        CodeReviewTelemetryContext context,
        string facet,
        string tool,
        string query
    )
    {
        using (context.BeginToolInvocationScope(facet))
        {
            context.RecordMissedQuery(tool, query);
        }
    }

    private static ToolKnowledgeSource Section(
        string path,
        string heading,
        string documentId,
        string snippetId,
        string? query = null,
        int rank = 0,
        double score = 0,
        bool im = false
    ) =>
        new(
            ToolName: "search_sections",
            Kind: ToolKnowledgeSourceKind.Section,
            Usage: ToolKnowledgeSourceUsage.Searched,
            Library: "kb",
            DocumentPath: path,
            DocumentTitle: "Doc " + documentId,
            Heading: heading,
            Language: null,
            Query: query,
            DocumentId: documentId,
            SnippetId: snippetId,
            Rank: rank,
            Score: score,
            IdentifierMatch: im
        );

    private static ToolKnowledgeSource Code(
        string path,
        string heading,
        string documentId,
        string snippetId,
        string? query = null
    ) =>
        new(
            ToolName: "search_code_snippets",
            Kind: ToolKnowledgeSourceKind.CodeSample,
            Usage: ToolKnowledgeSourceUsage.Searched,
            Library: "kb",
            DocumentPath: path,
            DocumentTitle: "Doc " + documentId,
            Heading: heading,
            Language: "csharp",
            Query: query,
            DocumentId: documentId,
            SnippetId: snippetId
        );

    private static ToolKnowledgeSource DocSearch(
        string path,
        string documentId,
        string? query = null,
        int rank = 0
    ) =>
        new(
            ToolName: "search_documents",
            Kind: ToolKnowledgeSourceKind.Document,
            Usage: ToolKnowledgeSourceUsage.Searched,
            Library: "kb",
            DocumentPath: path,
            DocumentTitle: "Doc " + documentId,
            Query: query,
            DocumentId: documentId,
            Rank: rank
        );

    private static ToolKnowledgeSource Read(string path, string documentId) =>
        new(
            ToolName: "read_document_by_path",
            Kind: ToolKnowledgeSourceKind.Document,
            Usage: ToolKnowledgeSourceUsage.Read,
            Library: "kb",
            DocumentPath: path,
            DocumentTitle: "Doc " + documentId,
            DocumentId: documentId
        );
}
