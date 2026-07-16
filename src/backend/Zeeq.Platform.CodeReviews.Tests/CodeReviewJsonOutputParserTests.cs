using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests the reviewer JSON output parser that replaced the XML extraction path.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewJsonOutputParserTests/*"
/// </summary>
public sealed class CodeReviewJsonOutputParserTests
{
    private static readonly CodeReviewerRuntimeAgent Agent = RuntimeAgent(
        "agent_structural",
        "Structural",
        "Structural Reviewer"
    );

    [Test]
    public async Task TryParse_WithValidJson_MapsModelAndStampsIdentity()
    {
        var json = """
            {
              "summary": "One injection risk found.",
              "details": "The endpoint binds user input without sanitizing it.",
              "findings": [
                {
                  "level": "CRITICAL",
                  "file": "src/Api/ImportCommand.cs",
                  "line": 42,
                  "side": "RIGHT",
                  "summary": "Unsanitized input",
                  "details": "The `Payload` field is bound directly."
                }
              ]
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out var facet, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(facet!.Facet).IsEqualTo("Structural");
        await Assert.That(facet.Agent).IsEqualTo("Structural Reviewer");
        await Assert.That(facet.Summary).IsEqualTo("One injection risk found.");
        await Assert
            .That(facet.Details)
            .IsEqualTo("The endpoint binds user input without sanitizing it.");

        var finding = facet.Findings.Single();
        await Assert.That(finding.Level).IsEqualTo(CodeReviewFindingLevel.Critical);
        await Assert.That(finding.File).IsEqualTo("src/Api/ImportCommand.cs");
        await Assert.That(finding.Line).IsEqualTo(42);
        await Assert.That(finding.Side).IsEqualTo("RIGHT");
        await Assert.That(finding.Summary).IsEqualTo("Unsanitized input");
        await Assert.That(finding.Details).IsEqualTo("The `Payload` field is bound directly.");
    }

    [Test]
    public async Task TryParse_WithMarkdownFenceAndPreamble_StripsWrapperAndParses()
    {
        var text = """
            Sure, here is the review:

            ```json
            {
              "summary": "Looks fine.",
              "details": "No blocking issues.",
              "findings": []
            }
            ```

            Let me know if you need anything else.
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(text, Agent, out var facet, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(facet!.Summary).IsEqualTo("Looks fine.");
        await Assert.That(facet.Findings).IsEmpty();
    }

    [Test]
    public async Task TryParse_WithEmptyFindings_IsValid()
    {
        var json = """
            {
              "summary": "LGTM 🚀",
              "details": "No actionable issues were found.",
              "findings": []
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out var facet, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(facet!.Findings).IsEmpty();
    }

    [Test]
    public async Task TryParse_WithLowercaseLevel_ParsesCaseInsensitively()
    {
        var json = """
            {
              "summary": "s",
              "details": "d",
              "findings": [
                { "level": "major", "file": "a.cs", "summary": "s", "details": "d" }
              ]
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out var facet, out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(facet!.Findings.Single().Level).IsEqualTo(CodeReviewFindingLevel.Major);
    }

    [Test]
    public async Task TryParse_WithOmittedLineAndSide_DefaultsToUnserializedValues()
    {
        var json = """
            {
              "summary": "s",
              "details": "d",
              "findings": [
                { "level": "COMMENT", "file": "a.cs", "summary": "s", "details": "d" }
              ]
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out var facet, out _);
        var finding = facet!.Findings.Single();

        await Assert.That(ok).IsTrue();
        // Line 0 and null Side are gated out of serialized XML by ShouldSerialize*.
        await Assert.That(finding.Line).IsEqualTo(0);
        await Assert.That(finding.ShouldSerializeLine()).IsFalse();
        await Assert.That(finding.Side).IsNull();
        await Assert.That(finding.ShouldSerializeSide()).IsFalse();
    }

    [Test]
    public async Task TryParse_WithNoJsonObject_FailsWithClearError()
    {
        var ok = CodeReviewJsonOutputParser.TryParse(
            "I could not produce a review.",
            Agent,
            out var facet,
            out var error
        );

        await Assert.That(ok).IsFalse();
        await Assert.That(facet).IsNull();
        await Assert.That(error).Contains("did not contain a JSON object");
    }

    [Test]
    public async Task TryParse_WithMalformedJson_FailsWithParseError()
    {
        var ok = CodeReviewJsonOutputParser.TryParse(
            """{ "summary": "s", "details": }""",
            Agent,
            out var facet,
            out var error
        );

        await Assert.That(ok).IsFalse();
        await Assert.That(facet).IsNull();
        await Assert.That(error).Contains("did not parse");
    }

    [Test]
    public async Task TryParse_WithMissingSummary_FailsWithSummaryError()
    {
        var ok = CodeReviewJsonOutputParser.TryParse(
            """{ "summary": "   ", "details": "d", "findings": [] }""",
            Agent,
            out _,
            out var error
        );

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("summary");
    }

    [Test]
    public async Task TryParse_WithMissingDetails_FailsWithDetailsError()
    {
        var ok = CodeReviewJsonOutputParser.TryParse(
            """{ "summary": "s", "findings": [] }""",
            Agent,
            out _,
            out var error
        );

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("details");
    }

    [Test]
    public async Task TryParse_WithFindingMissingFile_FailsWithIndexedError()
    {
        var json = """
            {
              "summary": "s",
              "details": "d",
              "findings": [
                { "level": "MINOR", "file": "", "summary": "s", "details": "d" }
              ]
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("findings[0]");
        await Assert.That(error).Contains("file");
    }

    [Test]
    public async Task TryParse_WithUnsupportedLevel_FailsWithLevelError()
    {
        var json = """
            {
              "summary": "s",
              "details": "d",
              "findings": [
                { "level": "BLOCKER", "file": "a.cs", "summary": "s", "details": "d" }
              ]
            }
            """;

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out _, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("BLOCKER");
    }

    [Test]
    public async Task TryParse_ThenSerialize_RoundTripsToValidCanonicalXml()
    {
        var json = """
            {
              "summary": "One issue.",
              "details": "Details with `code` and <angle> brackets.",
              "findings": [
                {
                  "level": "MAJOR",
                  "file": "src/A.cs",
                  "line": 7,
                  "summary": "Problem",
                  "details": "```cs\nvar x = 1;\n```"
                }
              ]
            }
            """;
        var validator = new CodeReviewXmlOutputValidator();

        var ok = CodeReviewJsonOutputParser.TryParse(json, Agent, out var facet, out _);
        var reviewBlock = validator.SerializeReviewerBlock(facet!);
        var validation = validator.ValidateReviewerBlock(reviewBlock);
        var roundTripped = validation.Output!.Reviews.Single();

        await Assert.That(ok).IsTrue();
        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(roundTripped.Facet).IsEqualTo("Structural");
        await Assert.That(roundTripped.Agent).IsEqualTo("Structural Reviewer");
        await Assert.That(roundTripped.Summary).IsEqualTo("One issue.");
        await Assert
            .That(roundTripped.Details)
            .IsEqualTo("Details with `code` and <angle> brackets.");

        var finding = roundTripped.Findings.Single();
        await Assert.That(finding.Level).IsEqualTo(CodeReviewFindingLevel.Major);
        await Assert.That(finding.File).IsEqualTo("src/A.cs");
        await Assert.That(finding.Line).IsEqualTo(7);
        await Assert.That(finding.Details).IsEqualTo("```cs\nvar x = 1;\n```");
    }

    private static CodeReviewerRuntimeAgent RuntimeAgent(
        string id,
        string facet,
        string displayName
    ) =>
        new(
            id,
            displayName,
            facet,
            CodeReviewModelTier.High,
            $"Review the PR for {facet}.",
            CodeReviewerActivationConfiguration.Empty
        );
}
