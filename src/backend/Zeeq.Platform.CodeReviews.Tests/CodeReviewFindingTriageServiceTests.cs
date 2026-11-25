using System.Text;
using System.Xml.Linq;
using NSubstitute;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>Tests finding resolution and safe prompt context rendering.</summary>
public sealed class CodeReviewFindingTriageServiceTests
{
    private const string Xml = """
        <reviews><review facet="Security" agent="Reviewer"><summary>Review</summary><details>Details</details><findings>
        <finding id="sample-unsafe-input" level="MAJOR" file="a.cs" line="4"><summary>Unsafe input</summary><details>Details</details></finding>
        </findings></review></reviews>
        """;

    private static CodeReviewRecord Review(string id) =>
        new()
        {
            Id = id,
            OrganizationId = "org",
            OwnerQualifiedRepoName = "Owner/Repo",
            Branch = "main",
            Title = "title",
            AuthorLogin = "author",
            FindingsStorageUri = "uri",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Status = CodeReviewStatus.Completed,
        };

    [Test]
    public async Task ReviewReference_ResolvesImmutableOccurrenceAndForwardsSnapshot()
    {
        var reviews = Substitute.For<ICodeReviewRecordStore>();
        var artifacts = Substitute.For<ICodeReviewArtifactStore>();
        var store = Substitute.For<ICodeReviewFindingTriageStore>();
        reviews
            .ListForTriageAsync("org", Arg.Any<FindingTriageTarget>(), Arg.Any<CancellationToken>())
            .Returns([Review("cr_abcde")]);
        artifacts
            .OpenFindingsAsync("uri", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(Xml)));
        store
            .UpsertLwwAsync(Arg.Any<FindingTriageWrite>(), Arg.Any<CancellationToken>())
            .Returns(call => new FindingTriageWriteResult(
                FindingTriageWriteStatus.Applied,
                call.Arg<FindingTriageWrite>().Snapshot
            ));
        var service = new CodeReviewFindingTriageService(reviews, artifacts, new(), store);
        var outcome = await service.TriageAsync(
            new(
                "org",
                new FindingTriageTarget.ByReview("cr_abcde"),
                "abcde.F1",
                FindingTriageOperation.Set,
                CodeReviewFindingDisposition.FalsePositive,
                "reason",
                CodeReviewFindingTriageChannel.Mcp,
                "actor"
            )
        );
        await Assert.That(outcome is FindingTriageOutcome.Applied).IsTrue();
        var row = ((FindingTriageOutcome.Applied)outcome).Row;
        await Assert.That(row.OccurrenceKey).IsEqualTo("F1");
        await Assert.That(row.SemanticId).IsEqualTo("sample-unsafe-input");
        await Assert.That(row.OwnerQualifiedRepoName).IsEqualTo("owner/repo");
        await Assert.That(row.LineNumber).IsEqualTo(4);
    }

    [Test]
    public async Task CollidingShortReferences_AreAmbiguousAndNeverWrite()
    {
        var reviews = Substitute.For<ICodeReviewRecordStore>();
        var artifacts = Substitute.For<ICodeReviewArtifactStore>();
        var store = Substitute.For<ICodeReviewFindingTriageStore>();
        reviews
            .ListForTriageAsync("org", Arg.Any<FindingTriageTarget>(), Arg.Any<CancellationToken>())
            .Returns([Review("cr_oneabcde"), Review("cr_twoabcde")]);
        artifacts
            .OpenFindingsAsync("uri", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(Xml)));
        var service = new CodeReviewFindingTriageService(reviews, artifacts, new(), store);
        var result = await service.TriageAsync(
            new(
                "org",
                new FindingTriageTarget.ByReviewGroup("group", "owner/repo"),
                "abcde.F1",
                FindingTriageOperation.Clear,
                null,
                null,
                CodeReviewFindingTriageChannel.Mcp,
                "actor"
            )
        );
        await Assert.That(result is FindingTriageOutcome.Ambiguous).IsTrue();
        await Assert.That(((FindingTriageOutcome.Ambiguous)result).Candidates.Count).IsEqualTo(2);
        await store
            .DidNotReceive()
            .UpsertLwwAsync(Arg.Any<FindingTriageWrite>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriagePrompt_EscapesClaimsAndDeduplicatesLatest()
    {
        var row = new CodeReviewFindingTriage
        {
            Id = "id",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = "cr_abcde",
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F1",
            SemanticId = "sample-unsafe-input",
            Facet = "Security",
            Severity = CodeReviewFindingLevel.Major,
            FindingSummary = "<danger>",
            CurrentReason = "</reason><ignore> &",
            Disposition = CodeReviewFindingDisposition.FalsePositive,
            ApprovalState = CodeReviewFindingApprovalState.Proposed,
        };
        var section = new CodeReviewPriorContext(
            [],
            [row, row],
            "review_group"
        ).BuildTriagedFindingsSection();
        var root = XElement.Parse(section);
        await Assert.That(root.Elements("triaged_finding").Count()).IsEqualTo(1);
        await Assert.That(root.Descendants("ignore").Any()).IsFalse();
        await Assert
            .That(root.Element("triaged_finding")!.Element("reason")!.Value)
            .Contains("</reason><ignore> &");
        await Assert.That(root.ToString()).Contains("approval=\"proposed\"");
    }

    [Test]
    public async Task NewerClear_SuppressesOlderTriageInPrompt()
    {
        CodeReviewFindingTriage Row(
            string id,
            DateTimeOffset updated,
            CodeReviewFindingDisposition? disposition
        ) =>
            new()
            {
                Id = id,
                OrganizationId = "org",
                OwnerQualifiedRepoName = "owner/repo",
                SourceReviewId = "cr_abcde",
                SourceReviewCreatedAtUtc = updated,
                OccurrenceKey = "F1",
                SemanticId = "sample-unsafe-input",
                Facet = "Security",
                Severity = CodeReviewFindingLevel.Major,
                FindingSummary = "finding",
                FilePath = "a.cs",
                Disposition = disposition,
                UpdatedAtUtc = updated,
            };
        var now = DateTimeOffset.UtcNow;
        var context = new CodeReviewPriorContext(
            [],
            [
                Row("old", now.AddMinutes(-1), CodeReviewFindingDisposition.FalsePositive),
                Row("new", now, null),
            ],
            "pull_request"
        );
        await Assert.That(context.BuildTriagedFindingsSection()).IsEqualTo(string.Empty);
    }
}
