namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for placeholder extraction and repository-scoped substitution in prompt documents.
/// </summary>
/// <remarks>
/// The substitution path runs on every MCP <c>prompts/get</c>, so these tests pin both behavior and
/// the allocation shape the retrieval path depends on (pass-through returns the original instance).
/// </remarks>
public sealed class PromptPlaceholderParserTests
{
    private static Dictionary<string, string> Overrides(params (string Name, string Value)[] values)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            overrides[name] = value;
        }

        return overrides;
    }

    // ── ContainsPlaceholders ────────────────────────────────────────────────

    [Test]
    public async Task ContainsPlaceholders_DocumentWithoutMarker_ReturnsFalse()
    {
        await Assert.That(PromptPlaceholderParser.ContainsPlaceholders("# Plain prompt")).IsFalse();
        await Assert.That(PromptPlaceholderParser.ContainsPlaceholders(null)).IsFalse();
        await Assert.That(PromptPlaceholderParser.ContainsPlaceholders("")).IsFalse();
    }

    [Test]
    public async Task ContainsPlaceholders_DocumentWithMarker_ReturnsTrue()
    {
        var content = """<zeeq_placeholder name="rules">Default</zeeq_placeholder>""";

        await Assert.That(PromptPlaceholderParser.ContainsPlaceholders(content)).IsTrue();
    }

    // ── Parse ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_AllAttributes_ReturnsTrimmedDefaultAndMetadata()
    {
        var content = """
            # Workflow

            <zeeq_placeholder name="testing_rules" label="Testing rules" description="How to test">
            Run the project's default test runner.
            </zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Name).IsEqualTo("testing_rules");
        await Assert.That(placeholders[0].Label).IsEqualTo("Testing rules");
        await Assert.That(placeholders[0].Description).IsEqualTo("How to test");
        await Assert
            .That(placeholders[0].DefaultValue)
            .IsEqualTo("Run the project's default test runner.");
    }

    [Test]
    public async Task Parse_ReorderedAndOmittedAttributes_StillResolvesName()
    {
        // Attribute order is authored by humans and must not be positional.
        var content = """
            <zeeq_placeholder description="Only a description" name="build_rules">Default</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Name).IsEqualTo("build_rules");
        await Assert.That(placeholders[0].Label).IsNull();
        await Assert.That(placeholders[0].Description).IsEqualTo("Only a description");
    }

    [Test]
    public async Task Parse_LabelBeforeName_DoesNotMistakeItForName()
    {
        // Explicit name remains the stable storage key even when the human label comes first.
        var content = """
            <zeeq_placeholder label="Display value" name="real_name">Default</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Name).IsEqualTo("real_name");
        await Assert.That(placeholders[0].Label).IsEqualTo("Display value");
    }

    [Test]
    public async Task Parse_LabelOnly_DerivesNameFromLabelSlug()
    {
        var content = """
            <zeeq_placeholder label="Language count">5</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Name).IsEqualTo("language-count");
        await Assert.That(placeholders[0].Label).IsEqualTo("Language count");
        await Assert.That(placeholders[0].DefaultValue).IsEqualTo("5");
    }

    [Test]
    public async Task Parse_DisplayNameOnly_IsIgnored()
    {
        var content = """
            <zeeq_placeholder displayName="Legacy label">Default</zeeq_placeholder>
            """;

        await Assert.That(PromptPlaceholderParser.Parse(content)).IsEmpty();
    }

    [Test]
    public async Task Parse_EmptyBody_ReturnsEmptyDefault()
    {
        var content = """<zeeq_placeholder name="optional"></zeeq_placeholder>""";

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].DefaultValue).IsEqualTo("");
    }

    [Test]
    public async Task Parse_MultiplePlaceholders_ReturnsAllInDocumentOrder()
    {
        var content = """
            <zeeq_placeholder name="first">One</zeeq_placeholder>
            Prose between.
            <zeeq_placeholder name="second">Two</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders.Select(p => p.Name)).IsEquivalentTo(["first", "second"]);
    }

    [Test]
    public async Task Parse_NonXmlSafeBody_IsTreatedAsOpaqueText()
    {
        // Prompt bodies are markdown: unescaped `<`, `&`, and code fences must survive.
        var content = """
            <zeeq_placeholder name="rules">
            Use `a < b && c > d` and:
            ```csharp
            var x = list[0];
            ```
            </zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].DefaultValue).Contains("a < b && c > d");
        await Assert.That(placeholders[0].DefaultValue).Contains("```csharp");
    }

    [Test]
    public async Task Parse_GreaterThanInsideQuotedAttribute_DoesNotTruncateTheTag()
    {
        var content = """
            <zeeq_placeholder name="rules" description="prefer > over <">Body</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Description).IsEqualTo("prefer > over <");
        await Assert.That(placeholders[0].DefaultValue).IsEqualTo("Body");
    }

    [Test]
    public async Task Parse_UnclosedTag_IsIgnored()
    {
        var content = """<zeeq_placeholder name="rules">Never closed""";

        await Assert.That(PromptPlaceholderParser.Parse(content)).IsEmpty();
    }

    [Test]
    public async Task Parse_UnclosedTag_DoesNotConsumeLaterValidPlaceholder()
    {
        var content = """
            <zeeq_placeholder name="first">unclosed
            <zeeq_placeholder name="second">Two</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(1);
        await Assert.That(placeholders[0].Name).IsEqualTo("second");
        await Assert.That(placeholders[0].DefaultValue).IsEqualTo("Two");
    }

    [Test]
    public async Task Parse_LazyBody_StopsAtFirstClosingTag()
    {
        var content = """
            <zeeq_placeholder name="first">One</zeeq_placeholder>middle<zeeq_placeholder name="second">Two</zeeq_placeholder>
            """;

        var placeholders = PromptPlaceholderParser.Parse(content);

        await Assert.That(placeholders).Count().IsEqualTo(2);
        await Assert.That(placeholders[0].DefaultValue).IsEqualTo("One");
        await Assert.That(placeholders[1].DefaultValue).IsEqualTo("Two");
    }

    // ── Substitute ──────────────────────────────────────────────────────────

    [Test]
    public async Task Substitute_NoOverrides_RendersDefaults()
    {
        var content = """
            Follow this workflow.

            <zeeq_placeholder name="rules" label="Rules">
            Use the default runner.
            </zeeq_placeholder>

            Then report.
            """;

        var result = PromptPlaceholderParser.Substitute(content, overrides: null);

        await Assert.That(result).DoesNotContain("zeeq_placeholder");
        await Assert.That(result).Contains("Use the default runner.");
        await Assert.That(result).Contains("Follow this workflow.");
        await Assert.That(result).Contains("Then report.");
    }

    [Test]
    public async Task Substitute_MatchingOverride_ReplacesDefault()
    {
        var content = """<zeeq_placeholder name="rules">Default rules</zeeq_placeholder>""";

        var result = PromptPlaceholderParser.Substitute(
            content,
            Overrides(("rules", "Repository specific rules"))
        );

        await Assert.That(result).IsEqualTo("Repository specific rules");
    }

    [Test]
    public async Task Substitute_PartialOverrides_MixesOverridesAndDefaults()
    {
        var content = """
            <zeeq_placeholder name="first">Default one</zeeq_placeholder>
            <zeeq_placeholder name="second">Default two</zeeq_placeholder>
            <zeeq_placeholder name="third">Default three</zeeq_placeholder>
            """;

        var result = PromptPlaceholderParser.Substitute(
            content,
            Overrides(("second", "Overridden two"))
        );

        await Assert.That(result).Contains("Default one");
        await Assert.That(result).Contains("Overridden two");
        await Assert.That(result).DoesNotContain("Default two");
        await Assert.That(result).Contains("Default three");
    }

    [Test]
    public async Task Substitute_NoValueAndNoDefault_YieldsEmptyString()
    {
        var content = """Before<zeeq_placeholder name="blank"></zeeq_placeholder>After""";

        var result = PromptPlaceholderParser.Substitute(content, overrides: null);

        await Assert.That(result).IsEqualTo("BeforeAfter");
    }

    [Test]
    public async Task Substitute_UnrelatedOverrides_LeaveDefaultsIntact()
    {
        var content = """<zeeq_placeholder name="rules">Default rules</zeeq_placeholder>""";

        var result = PromptPlaceholderParser.Substitute(
            content,
            Overrides(("some_other_placeholder", "Ignored"))
        );

        await Assert.That(result).IsEqualTo("Default rules");
    }

    [Test]
    public async Task Substitute_DuplicateNames_AllOccurrencesReceiveTheSameValue()
    {
        var content = """
            <zeeq_placeholder name="repeated">A</zeeq_placeholder>|<zeeq_placeholder name="repeated">B</zeeq_placeholder>
            """;

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("repeated", "X")));

        await Assert.That(result).IsEqualTo("X|X");
    }

    [Test]
    public async Task Substitute_OverrideLongerThanDefault_ProducesCorrectLength()
    {
        // Guards the exact-size string.Create path against an off-by-one in the length arithmetic.
        var content = """A<zeeq_placeholder name="n">x</zeeq_placeholder>B""";
        var replacement = new string('y', 5_000);

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("n", replacement)));

        await Assert.That(result).IsEqualTo($"A{replacement}B");
        await Assert.That(result.Length).IsEqualTo(5_002);
    }

    [Test]
    public async Task Substitute_MalformedRegion_LeavesTextUntouched()
    {
        var content = """Intro <zeeq_placeholder name="rules">never closed""";

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("rules", "value")));

        await Assert.That(result).IsEqualTo(content);
    }

    [Test]
    public async Task Substitute_UnclosedTag_DoesNotConsumeLaterValidPlaceholder()
    {
        var content = """
            Intro <zeeq_placeholder name="first">unclosed <zeeq_placeholder name="second">Two</zeeq_placeholder> Outro
            """;

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("second", "Override")));

        await Assert
            .That(result)
            .Contains("""Intro <zeeq_placeholder name="first">unclosed Override Outro""");
        await Assert.That(result).DoesNotContain("name=\"second\"");
        await Assert.That(result).DoesNotContain("Two");
    }

    [Test]
    public async Task Substitute_LabelOnly_DerivesNameFromLabelSlug()
    {
        var content = """
            Intro <zeeq_placeholder label="Rules">body</zeeq_placeholder> Outro
            """;

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("rules", "value")));

        await Assert.That(result).IsEqualTo("Intro value Outro");
    }

    [Test]
    public async Task Substitute_ExplicitNameWinsOverDerivedLabelSlug()
    {
        var content = """
            <zeeq_placeholder name="stable-key" label="Editable label">Default</zeeq_placeholder>
            """;

        var result = PromptPlaceholderParser.Substitute(
            content,
            Overrides(("editable-label", "Wrong"), ("stable-key", "Right"))
        );

        await Assert.That(result).IsEqualTo("Right");
    }

    [Test]
    public async Task Substitute_MalformedOpeningTag_LeavesTextUntouched()
    {
        var content = """
            Intro <zeeq_placeholder-not-a-tag>body</zeeq_placeholder> Outro
            """;

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("rules", "value")));

        await Assert.That(result).IsEqualTo(content);
    }

    [Test]
    public async Task Substitute_DeserializedComparerDictionary_StillAppliesOverrides()
    {
        // A dictionary round-tripped through a cache/serializer does not carry the ordinal comparer
        // the span lookup needs. Overrides must still apply rather than being silently dropped.
        var content = """<zeeq_placeholder name="rules">Default</zeeq_placeholder>""";
        var deserialized = new Dictionary<string, string> { ["rules"] = "From cache" };

        var result = PromptPlaceholderParser.Substitute(content, deserialized);

        await Assert.That(result).IsEqualTo("From cache");
    }

    [Test]
    public async Task Substitute_ManyPlaceholders_GrowsPooledBufferCorrectly()
    {
        // Exceeds the initial rented capacity so the growth path is exercised.
        var content = string.Concat(
            Enumerable
                .Range(0, 40)
                .Select(index =>
                    $"""<zeeq_placeholder name="p{index}">d{index}</zeeq_placeholder>|"""
                )
        );

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("p39", "LAST")));

        await Assert.That(result).DoesNotContain("zeeq_placeholder");
        await Assert.That(result).StartsWith("d0|d1|");
        await Assert.That(result).EndsWith("LAST|");
    }

    // ── Allocation invariants ───────────────────────────────────────────────

    [Test]
    public async Task Substitute_DocumentWithoutPlaceholders_ReturnsSameInstance()
    {
        // The retrieval path relies on this: a plain prompt must cost no allocation and no copy.
        var content = "# Plain prompt with no templating at all.";

        var result = PromptPlaceholderParser.Substitute(content, Overrides(("unused", "value")));

        await Assert.That(ReferenceEquals(result, content)).IsTrue();
    }

    [Test]
    public async Task Substitute_MarkerPresentButNothingWellFormed_ReturnsSameInstance()
    {
        var content = "Mentions <zeeq_placeholder but never opens a real region.";

        var result = PromptPlaceholderParser.Substitute(content, overrides: null);

        await Assert.That(ReferenceEquals(result, content)).IsTrue();
    }

    [Test]
    public async Task Substitute_SubstitutingCall_AllocatesExactlyOneString()
    {
        var content = string.Concat(
            Enumerable
                .Range(0, 8)
                .Select(index =>
                    $"""padding padding<zeeq_placeholder name="p{index}">default {index}</zeeq_placeholder>"""
                )
        );
        var overrides = Overrides(("p0", "value"), ("p3", "value"), ("p7", "value"));

        // Warm the generated regex and pooled buffers so steady-state behavior is measured.
        _ = PromptPlaceholderParser.Substitute(content, overrides);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = PromptPlaceholderParser.Substitute(content, overrides);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // One string of `result.Length` chars plus object header/length overhead. A per-placeholder
        // string (name, default, or a StringBuilder chunk) would push this well past the bound.
        var singleStringUpperBound = (result.Length * sizeof(char)) + 64;

        await Assert.That(allocated).IsGreaterThan(0);
        await Assert.That(allocated).IsLessThanOrEqualTo(singleStringUpperBound);
    }
}
