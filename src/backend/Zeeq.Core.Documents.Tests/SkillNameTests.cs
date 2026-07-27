namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for the skill prompt name value type used by MCP dynamic prompts.
/// </summary>
public sealed class SkillNameTests
{
    [Test]
    public async Task Short_NormalizesSkillName()
    {
        var name = SkillName.Short("Dotnet C# Best Practices");

        await Assert.That(name.Value).IsEqualTo("dotnet-c-best-practices");
        await Assert.That(name.NormalizedName).IsEqualTo("dotnet-c-best-practices");
        await Assert.That(name.IsFullyQualified).IsFalse();
        await Assert.That(name.Library).IsNull();
        await Assert.That(name.DocumentPathSegments).IsEmpty();
        await Assert.That(name.ToString()).IsEqualTo("dotnet-c-best-practices");
    }

    [Test]
    public async Task Short_PunctuationOnlySkillName_ThrowsArgumentException()
    {
        SkillName Act() => SkillName.Short("---");

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task FullyQualified_NormalizesLibraryPathAndSkillName()
    {
        var name = SkillName.FullyQualified(
            library: "Zeeq App",
            documentPath: "/Backend/Dotnet-CSharp-Best-Practices.md",
            skillName: "Review Guidance"
        );

        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:backend/dotnet-csharp-best-practices[review-guidance]");
        await Assert.That(name.IsFullyQualified).IsTrue();
        await Assert.That(name.Library).IsEqualTo("zeeq-app");
        await Assert
            .That(name.DocumentPathSegments)
            .IsEquivalentTo(["backend", "dotnet-csharp-best-practices"]);
        await Assert.That(name.NormalizedName).IsEqualTo("review-guidance");
        await Assert.That(name.ShortDocumentId).IsNull();
    }

    [Test]
    public async Task FullyQualified_DocumentPathWithoutLeadingSlashOrExtension_NormalizesPath()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq-app",
            documentPath: "backend/web-api-endpoints-openapi",
            skillName: "API Skill"
        );

        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:backend/web-api-endpoints-openapi[api-skill]");
    }

    [Test]
    public async Task FullyQualified_SkillNameSameAsFinalPathSegment_OmitsBracketSuffix()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq-app",
            documentPath: "/backend/dotnet-csharp-best-practices.md",
            skillName: "dotnet-csharp-best-practices"
        );

        await Assert.That(name.Value).IsEqualTo("zeeq-app:backend/dotnet-csharp-best-practices");
    }

    [Test]
    public async Task FullyQualified_SkillMdDocument_UsesParentDirectoryAsPathIdentity()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq-app",
            documentPath: "/.agents/skills/zeeq-dotnet-repl/SKILL.md",
            skillName: "zeeq-dotnet-repl"
        );

        await Assert.That(name.Value).IsEqualTo("zeeq-app:agents/skills/zeeq-dotnet-repl");
        await Assert
            .That(name.DocumentPathSegments)
            .IsEquivalentTo(["agents", "skills", "zeeq-dotnet-repl"]);
    }

    [Test]
    public async Task FullyQualified_SkillMdDocumentWithDifferentSkillName_AppendsBracketSuffix()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq-app",
            documentPath: "/.agents/skills/zeeq-dotnet-repl/SKILL.md",
            skillName: "csharp-repl-runtime-debugging"
        );

        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:agents/skills/zeeq-dotnet-repl[csharp-repl-runtime-debugging]");
    }

    [Test]
    public async Task FullyQualified_ShortDocumentId_AddsFinalBracketTieBreaker()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq-app",
            documentPath: "/backend/dotnet-csharp-best-practices.md",
            skillName: "Review Guidance",
            shortDocumentId: "ABC_123"
        );

        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:backend/dotnet-csharp-best-practices[review-guidance][abc_123]");
        await Assert.That(name.ShortDocumentId).IsEqualTo("abc_123");
    }

    [Test]
    public async Task FullyQualified_RawStructuralCharactersInsideSegments_NormalizesBeforeRendering()
    {
        var name = SkillName.FullyQualified(
            library: "zeeq:app",
            documentPath: "/backend/dotnet[core].md",
            skillName: "review[guidance]"
        );

        await Assert.That(name.Value).IsEqualTo("zeeq-app:backend/dotnetcore[review-guidance]");
    }

    [Test]
    public async Task FullyQualified_SlashesInsideAtomicSegments_NormalizeToDashes()
    {
        var name = SkillName.FullyQualified(
            library: "team/backend",
            documentPath: "/backend/guide.md",
            skillName: "review/guidance",
            shortDocumentId: "abc/123"
        );

        await Assert.That(name.Library).IsEqualTo("team-backend");
        await Assert.That(name.NormalizedName).IsEqualTo("review-guidance");
        await Assert.That(name.ShortDocumentId).IsEqualTo("abc-123");
        await Assert
            .That(name.Value)
            .IsEqualTo("team-backend:backend/guide[review-guidance][abc-123]");
    }

    [Test]
    public async Task TryParse_ShortName_RoundTripsAsShortName()
    {
        var parsed = SkillName.TryParse("dotnet-csharp-best-practices", out var name);

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.Value).IsEqualTo("dotnet-csharp-best-practices");
        await Assert.That(name.IsFullyQualified).IsFalse();
        await Assert.That(name.NormalizedName).IsEqualTo("dotnet-csharp-best-practices");
    }

    [Test]
    public async Task TryParse_FullyQualifiedName_RoundTripsAllLookupSegments()
    {
        var parsed = SkillName.TryParse(
            "zeeq-app:docs/backend/dotnet-csharp-best-practices[review-guidance]",
            out var name
        );

        await Assert.That(parsed).IsTrue();
        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:docs/backend/dotnet-csharp-best-practices[review-guidance]");
        await Assert.That(name.IsFullyQualified).IsTrue();
        await Assert.That(name.Library).IsEqualTo("zeeq-app");
        await Assert
            .That(name.DocumentPathSegments)
            .IsEquivalentTo(["docs", "backend", "dotnet-csharp-best-practices"]);
        await Assert.That(name.NormalizedName).IsEqualTo("review-guidance");
        await Assert.That(name.ShortDocumentId).IsNull();
    }

    [Test]
    public async Task TryParse_FullyQualifiedNameWithShortDocumentId_RoundTripsSuffix()
    {
        var parsed = SkillName.TryParse(
            "zeeq-app:backend/dotnet-csharp-best-practices[review-guidance][abc_123]",
            out var name
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.NormalizedName).IsEqualTo("review-guidance");
        await Assert.That(name.ShortDocumentId).IsEqualTo("abc_123");
        await Assert
            .That(name.Value)
            .IsEqualTo("zeeq-app:backend/dotnet-csharp-best-practices[review-guidance][abc_123]");
    }

    [Test]
    public async Task TryParse_SlashesInsideAtomicSegments_NormalizeToDashes()
    {
        var parsed = SkillName.TryParse(
            "team/backend:docs/api[review/guidance][abc/123]",
            out var name
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.Library).IsEqualTo("team-backend");
        await Assert.That(name.DocumentPathSegments).IsEquivalentTo(["docs", "api"]);
        await Assert.That(name.NormalizedName).IsEqualTo("review-guidance");
        await Assert.That(name.ShortDocumentId).IsEqualTo("abc-123");
        await Assert.That(name.Value).IsEqualTo("team-backend:docs/api[review-guidance][abc-123]");
    }

    [Test]
    public async Task TryParse_FullyQualifiedNameWithoutBracket_UsesFinalPathSegmentAsSkillName()
    {
        var parsed = SkillName.TryParse("zeeq-app:agents/skills/zeeq-dotnet-repl", out var name);

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.NormalizedName).IsEqualTo("zeeq-dotnet-repl");
        await Assert.That(name.Value).IsEqualTo("zeeq-app:agents/skills/zeeq-dotnet-repl");
    }

    [Test]
    public async Task TryParse_DottedDocumentStemSegment_NormalizesInsteadOfRejecting()
    {
        var parsed = SkillName.TryParse("zeeq-app:guides/api.v2[api-v2]", out var name);

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.Value).IsEqualTo("zeeq-app:guides/api-v2");
        await Assert.That(name.DocumentPathSegments).IsEquivalentTo(["guides", "api-v2"]);
    }

    [Test]
    public async Task TryParse_UnnormalizedFullyQualifiedName_ReturnsNormalizedName()
    {
        var parsed = SkillName.TryParse(
            "Zeeq App:Backend/Review Guidance[Skill Name]",
            out var name
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(name.Value).IsEqualTo("zeeq-app:backend/review-guidance[skill-name]");
    }

    [Test]
    public async Task TryParse_MissingLibrary_ReturnsFalse()
    {
        var parsed = SkillName.TryParse(":backend/review-guidance", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParse_MissingPath_ReturnsFalse()
    {
        var parsed = SkillName.TryParse("zeeq-app:", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParse_MalformedBracketSuffix_ReturnsFalse()
    {
        var parsed = SkillName.TryParse("zeeq-app:backend/review-guidance[skill", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryParse_EmptyBracketSuffix_ReturnsFalse()
    {
        var parsed = SkillName.TryParse("zeeq-app:backend/review-guidance[]", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task FullyQualified_PathSegmentEmptyAfterNormalization_ThrowsArgumentException()
    {
        SkillName Act() =>
            SkillName.FullyQualified(
                library: "zeeq-app",
                documentPath: "/backend/---/skill.md",
                skillName: "Review Guidance"
            );

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task FullyQualified_RootSkillMdDocument_ThrowsArgumentException()
    {
        SkillName Act() =>
            SkillName.FullyQualified(
                library: "zeeq-app",
                documentPath: "/SKILL.md",
                skillName: "Skill"
            );

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task TryParse_BlankValue_ReturnsFalse()
    {
        var parsed = SkillName.TryParse(" ", out _);

        await Assert.That(parsed).IsFalse();
    }
}
