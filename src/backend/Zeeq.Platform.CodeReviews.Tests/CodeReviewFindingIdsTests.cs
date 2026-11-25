using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>Locks V1 finding identity and document ordering.</summary>
public class CodeReviewFindingIdsTests
{
    [Test]
    [Arguments("Null", "src/Handler.cs", "Security", "null-handler-security")]
    [Arguments("Missing null check", "src/Handler.cs", "Security", "missing-null-check-handler")]
    [Arguments("漢字", "", "Security", "security-finding-1")]
    [Arguments("123Unsafe input", "src/Handler.cs", "Security", "unsafe-input-handler")]
    [Arguments("", "", "", "review-finding-1")]
    public async Task Assign_LegacyGoldenVectors(
        string summary,
        string file,
        string facet,
        string expected
    )
    {
        var document = new CodeReviewOutputDocument
        {
            Reviews =
            [
                new() { Facet = facet, Findings = [new() { Summary = summary, File = file }] },
            ],
        };
        await Assert.That(CodeReviewFindingIds.Assign(document)[0].SemanticId).IsEqualTo(expected);
    }

    [Test]
    public async Task Assign_StampedIdsAndCollisions_RemainStable()
    {
        var document = new CodeReviewOutputDocument
        {
            Reviews =
            [
                new()
                {
                    Facet = "Security",
                    Findings =
                    [
                        new() { Id = "unsafe-input-handler", Summary = "changed" },
                        new() { Id = "unsafe-input-handler-2", Summary = "changed" },
                        new() { Id = "unsafe-input-handler", Summary = "changed" },
                    ],
                },
            ],
        };
        var ids = CodeReviewFindingIds.Assign(document);
        await Assert.That(ids[0].SemanticId).IsEqualTo("unsafe-input-handler");
        await Assert.That(ids[1].SemanticId).IsEqualTo("unsafe-input-handler-2");
        await Assert.That(ids[2].SemanticId).IsEqualTo("unsafe-input-handler-3");
    }

    [Test]
    public async Task Assign_LegacyFindings_DerivesStableUniqueIdsInDocumentOrder()
    {
        var document = new CodeReviewOutputDocument
        {
            Reviews =
            [
                new()
                {
                    Facet = "Security",
                    Findings =
                    [
                        new() { Summary = "Null", File = "src/Handler.cs" },
                        new() { Summary = "Null", File = "src/Handler.cs" },
                        new() { Summary = "123 漢字", File = "" },
                    ],
                },
            ],
        };
        var assigned = CodeReviewFindingIds.Assign(document);
        await Assert
            .That(assigned.Select(x => x.SemanticId).ToArray())
            .IsEquivalentTo(
                new[] { "null-handler-security", "null-handler-security-2", "security-finding-3" }
            );
        await Assert.That(assigned[1].OccurrenceKey).IsEqualTo("F2");
        await Assert
            .That(
                CodeReviewFindingIds.FormatShortReference("cr_123ABCDEF", assigned[1].OccurrenceKey)
            )
            .IsEqualTo("bcdef.F2");
    }
}
