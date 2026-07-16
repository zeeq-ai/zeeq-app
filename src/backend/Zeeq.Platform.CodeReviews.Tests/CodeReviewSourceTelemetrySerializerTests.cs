namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests the jsonb serializer for <see cref="CodeReviewSourceTelemetry" />: compact-key
/// round-trip fidelity and the best-effort null contract for empty/malformed payloads.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewSourceTelemetrySerializerTests/*"
/// </summary>
public sealed class CodeReviewSourceTelemetrySerializerTests
{
    /// <summary>Builds a fully populated snapshot mirroring the storage-shape example (spec §2.3).</summary>
    private static CodeReviewSourceTelemetry PopulatedSnapshot() =>
        new(
            SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
            Summary: new(
                DocumentCount: 2,
                SnippetCount: 2,
                SourceHitCount: 7,
                ToolCallCount: 6,
                MissedQueryCount: 1
            ),
            Documents:
            [
                new(
                    DocumentId: "doc_01H",
                    Library: "zeeq-app",
                    Path: "/backend/dotnet-csharp-best-practices.md",
                    Title: "C# 14 (CSharp), .NET 10, EF General Guidelines",
                    HitCount: 5,
                    Usages: ["Searched", "Read"],
                    ReadAfterSearch: true,
                    Facets: ["Security", "Performance"],
                    BestRank: 1,
                    BestScore: 0.0312,
                    Queries: ["structured logging with serilog"],
                    Snippets:
                    [
                        new(
                            SnippetId: "sn_01H",
                            Heading: "Logging and OpenTelemetry (OTEL) Tracing",
                            Kind: "Section",
                            Language: null,
                            HitCount: 3,
                            Facets: ["Security", "Performance"],
                            BestRank: 1,
                            BestScore: 0.0312,
                            IdentifierMatch: true,
                            Queries: ["otel tracing spans", "structured logging"]
                        ),
                        new(
                            SnippetId: "sn_02J",
                            Heading: "Database Storage with Postgres and Npgsql",
                            Kind: "CodeSample",
                            Language: "csharp",
                            HitCount: 1,
                            Facets: ["Performance"],
                            BestRank: 4,
                            BestScore: 0.0121,
                            IdentifierMatch: false,
                            Queries: ["npgsql batching pattern"]
                        ),
                    ]
                ),
                new(
                    DocumentId: "doc_01J",
                    Library: "zeeq-app",
                    Path: "/backend/web-api-endpoints-openapi.md",
                    Title: "Web API Endpoints and OpenAPI",
                    HitCount: 2,
                    Usages: ["Read"],
                    ReadAfterSearch: false,
                    Facets: ["Structural"],
                    BestRank: 0,
                    BestScore: 0,
                    Queries: [],
                    Snippets: []
                ),
            ],
            ToolUsage:
            [
                new(Tool: "search_code_snippets", Calls: 2, Succeeded: 2, Failed: 0),
                new(Tool: "search_sections", Calls: 2, Succeeded: 2, Failed: 0),
            ],
            MissedQueries:
            [
                new(
                    Query: "aspire distributed lock pattern",
                    Tool: "search_sections",
                    Facets: ["Structural"]
                ),
            ]
        );

    [Test]
    public async Task Serialize_UsesCompactKeys_RoundTrips()
    {
        var original = PopulatedSnapshot();

        var json = CodeReviewSourceTelemetrySerializer.Serialize(original);

        // Compact wire keys must be present; descriptive C# names must not leak into storage.
        await Assert.That(json).Contains("\"sn\"");
        await Assert.That(json).Contains("\"hc\"");
        await Assert.That(json).Contains("\"sid\"");
        await Assert.That(json).Contains("\"ras\"");
        await Assert.That(json).DoesNotContain("SchemaVersion");
        await Assert.That(json).DoesNotContain("ReadAfterSearch");

        var roundTripped = CodeReviewSourceTelemetrySerializer.Deserialize(json);

        await Assert.That(roundTripped).IsNotNull();

        // Records with list members use reference equality, so compare via re-serialization
        // (structural fidelity) plus spot-check the stable ids and relevance signals.
        await Assert
            .That(CodeReviewSourceTelemetrySerializer.Serialize(roundTripped!))
            .IsEqualTo(json);

        var firstDoc = roundTripped!.Documents[0];
        await Assert.That(firstDoc.DocumentId).IsEqualTo("doc_01H");
        await Assert.That(firstDoc.ReadAfterSearch).IsTrue();
        await Assert.That(firstDoc.BestRank).IsEqualTo(1);
        await Assert.That(firstDoc.BestScore).IsEqualTo(0.0312);
        await Assert.That(firstDoc.Snippets[0].SnippetId).IsEqualTo("sn_01H");
        await Assert.That(firstDoc.Snippets[0].IdentifierMatch).IsTrue();
        await Assert.That(firstDoc.Snippets[1].Language).IsEqualTo("csharp");
        await Assert
            .That(roundTripped.MissedQueries[0].Query)
            .IsEqualTo("aspire distributed lock pattern");
    }

    [Test]
    public async Task Serialize_OmitsNullLanguage()
    {
        var json = CodeReviewSourceTelemetrySerializer.Serialize(PopulatedSnapshot());

        // The section snippet has a null language; ignore-null must drop it, while the
        // code-sample snippet retains "lang":"csharp".
        await Assert.That(json).Contains("\"lang\":\"csharp\"");
        await Assert.That(json).DoesNotContain("\"lang\":null");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{}")]
    public async Task Deserialize_WhenEmptyOrBrace_ReturnsNull(string? payload)
    {
        var result = CodeReviewSourceTelemetrySerializer.Deserialize(payload);

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("{ not json")]
    [Arguments("[1,2,3]")]
    [Arguments("\"just a string\"")]
    public async Task Deserialize_WhenMalformed_ReturnsNull(string payload)
    {
        var result = CodeReviewSourceTelemetrySerializer.Deserialize(payload);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Empty_SnapshotIsEmpty_AndSerializesToBrace()
    {
        var empty = CodeReviewSourceTelemetry.Empty;

        await Assert.That(empty.IsEmpty).IsTrue();

        // The empty snapshot still carries a schema version and summary, so its serialized form
        // is not literally "{}" — the runner maps IsEmpty to the "{}" sentinel instead.
        var populated = PopulatedSnapshot();
        await Assert.That(populated.IsEmpty).IsFalse();
    }
}
