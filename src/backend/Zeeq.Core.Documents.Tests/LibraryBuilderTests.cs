using Zeeq.Core.Documents;

namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for the <see cref="LibraryBuilder"/> fluent constructor.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Documents.Tests --output detailed --disable-logo --treenode-filter "/*/*/LibraryBuilderTests/*"
/// </summary>
public sealed class LibraryBuilderTests
{
    private static string NewId() => $"test-{Guid.NewGuid():N}"[..16];

    // ─── ForLocal variant ──────────────────────────────────────────────

    [Test]
    public async Task ForLocal_Build_ProducesLocalOnlyColumns()
    {
        // Guards that a local library has no source linkage — no
        // PublicSourceId, no SourceKind, empty filter arrays, and no
        // sync lifecycle fields. This is the default state for
        // hand-authored libraries.
        var library = LibraryBuilder.ForLocal().Build(NewId(), "org_a", "my-docs");

        await Assert.That(library.PublicSourceId).IsNull();
        await Assert.That(library.SourceKind).IsNull();
        await Assert.That(library.SourceRepoUrl).IsNull();
        await Assert.That(library.IncludeFilters).IsEmpty();
        await Assert.That(library.ExcludeFilters).IsEmpty();
        await Assert.That(library.SourceDefaultIncludeFilters).IsEmpty();
        await Assert.That(library.SourceDefaultExcludeFilters).IsEmpty();
        await Assert.That(library.SyncStatus).IsNull();
        await Assert.That(library.NextSyncAt).IsNull();
    }

    [Test]
    public async Task ForLocal_BuildFrom_PreservesIdentityAndMutatesFields()
    {
        // Guards that BuildFrom on a local library copies identity,
        // team, and timestamps from the existing row while only
        // mutating Name, Description, and UpdatedAt. No source
        // linkage fields appear.
        var existing = LibraryBuilder
            .ForLocal()
            .Build(
                id: NewId(),
                organizationId: "org_a",
                name: "old-name",
                teamId: "team_1",
                description: "old desc"
            );

        var updated = LibraryBuilder
            .ForLocal()
            .BuildFrom(existing, name: "new-name", description: "new desc");

        await Assert.That(updated.Id).IsEqualTo(existing.Id);
        await Assert.That(updated.OrganizationId).IsEqualTo(existing.OrganizationId);
        await Assert.That(updated.TeamId).IsEqualTo(existing.TeamId);
        await Assert.That(updated.Name).IsEqualTo("new-name");
        await Assert.That(updated.Description).IsEqualTo("new desc");
        await Assert.That(updated.CreatedAt).IsEqualTo(existing.CreatedAt);
        await Assert.That(updated.UpdatedAt).IsGreaterThan(existing.UpdatedAt);
        await Assert.That(updated.PublicSourceId).IsNull();
        await Assert.That(updated.SourceKind).IsNull();
    }

    // ─── ForPublicSource variant ───────────────────────────────────────

    [Test]
    public async Task ForPublicSource_Build_ProducesPublicColumns()
    {
        // Guards that a public-source library sets PublicSourceId,
        // leaves SourceKind null (mutually exclusive), and populates
        // the library-level filter arrays. Source defaults are empty
        // since they come from docs_public_sources, not the library row.
        var library = LibraryBuilder
            .ForPublicSource("src_dotnet-aspire")
            .WithIncludeFilter("aspire/**")
            .WithExcludeFilter("**/internal/**")
            .Build(NewId(), "org_a", "dotnet-docs");

        await Assert.That(library.PublicSourceId).IsEqualTo("src_dotnet-aspire");
        await Assert.That(library.SourceKind).IsNull();
        await Assert.That(library.IncludeFilters).IsEquivalentTo(["aspire/**"]);
        await Assert.That(library.ExcludeFilters).IsEquivalentTo(["**/internal/**"]);
        // Source-level defaults should remain empty — they live on the
        // DocsPublicSource row, not on the library.
        await Assert.That(library.SourceDefaultIncludeFilters).IsEmpty();
        await Assert.That(library.SourceDefaultExcludeFilters).IsEmpty();
    }

    [Test]
    public async Task ForPublicSource_BuildFrom_PreservesLifecycle()
    {
        // Guards that BuildFrom on a public-source library carries
        // forward sync lifecycle state and manual trigger history
        // from the existing row. These fields are managed by the
        // scheduler and runner, not by user-facing update handlers.
        var existing = new Library
        {
            Id = NewId(),
            OrganizationId = "org_a",
            Name = "old-name",
            PublicSourceId = "src_dotnet-aspire",
            IncludeFilters = ["aspire/**"],
            ExcludeFilters = [],
            SyncStatus = "idle",
            NextSyncAt = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero),
            ManualTriggerHistory = [new DateTimeOffset(2026, 7, 9, 17, 0, 0, TimeSpan.Zero)],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var updated = LibraryBuilder
            .ForPublicSource(existing.PublicSourceId!)
            .BuildFrom(existing, name: "new-name");

        await Assert.That(updated.Name).IsEqualTo("new-name");
        await Assert.That(updated.SyncStatus).IsEqualTo("idle");
        await Assert.That(updated.NextSyncAt).IsEqualTo(existing.NextSyncAt);
        await Assert
            .That(updated.ManualTriggerHistory)
            .IsEquivalentTo(existing.ManualTriggerHistory);
        await Assert.That(updated.PublicSourceId).IsEqualTo("src_dotnet-aspire");
        await Assert.That(updated.IncludeFilters).IsEquivalentTo(["aspire/**"]);
    }

    [Test]
    public async Task ForPublicSource_MultipleIncludeFilters_Appends()
    {
        // Guards that chained WithIncludeFilter calls append to the
        // array rather than replacing — one path per entry for
        // multiple discrete subtrees.
        var library = LibraryBuilder
            .ForPublicSource("src_dotnet-docs")
            .WithIncludeFilter("aspire/**")
            .WithIncludeFilter("csharp/**")
            .Build(NewId(), "org_a", "dotnet-docs");

        await Assert.That(library.IncludeFilters).IsEquivalentTo(["aspire/**", "csharp/**"]);
    }

    // ─── ForPrivateSource variant ──────────────────────────────────────

    [Test]
    public async Task ForPrivateSource_Build_ProducesPrivateColumns()
    {
        // Guards that a private-source library has SourceKind and
        // SourceRepoUrl set, PublicSourceId null, and both library-
        // level and source-default filter arrays populated.
        var library = LibraryBuilder
            .ForPrivateSource(
                sourceKind: "GitHub",
                sourceRepoUrl: "https://github.com/acme/internal-docs"
            )
            .WithDefaultIncludeFilter("**/*.md")
            .WithDefaultExcludeFilter("_drafts/**")
            .WithIncludeFilter("docs/**")
            .WithExcludeFilter("**/internal/**")
            .Build(NewId(), "org_a", "internal-docs");

        await Assert.That(library.PublicSourceId).IsNull();
        await Assert.That(library.SourceKind).IsEqualTo("GitHub");
        await Assert.That(library.SourceRepoUrl).IsEqualTo("https://github.com/acme/internal-docs");
        await Assert.That(library.SourceDefaultIncludeFilters).IsEquivalentTo(["**/*.md"]);
        await Assert.That(library.SourceDefaultExcludeFilters).IsEquivalentTo(["_drafts/**"]);
        await Assert.That(library.IncludeFilters).IsEquivalentTo(["docs/**"]);
        await Assert.That(library.ExcludeFilters).IsEquivalentTo(["**/internal/**"]);
    }

    [Test]
    public async Task ForPrivateSource_BuildFrom_PreservesSourceMetadata()
    {
        // Guards that BuildFrom on a private-source library carries
        // forward source metadata and lifecycle state, handling
        // the full set of private source columns.
        var existing = new Library
        {
            Id = NewId(),
            OrganizationId = "org_a",
            Name = "old-name",
            SourceKind = "GitHub",
            SourceRepoUrl = "https://github.com/acme/internal-docs",
            SourceDefaultIncludeFilters = ["**/*.md"],
            SourceDefaultExcludeFilters = [],
            IncludeFilters = ["docs/**"],
            ExcludeFilters = ["**/draft/**"],
            SourceSyncedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SyncStatus = "idle",
            NextSyncAt = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var updated = LibraryBuilder
            .ForPrivateSource(existing.SourceKind!, existing.SourceRepoUrl!)
            .BuildFrom(existing, name: "updated-name");

        await Assert.That(updated.Name).IsEqualTo("updated-name");
        await Assert.That(updated.SourceKind).IsEqualTo("GitHub");
        await Assert.That(updated.SourceRepoUrl).IsEqualTo(existing.SourceRepoUrl);
        await Assert.That(updated.SourceDefaultIncludeFilters).IsEquivalentTo(["**/*.md"]);
        await Assert.That(updated.IncludeFilters).IsEquivalentTo(["docs/**"]);
        await Assert.That(updated.ExcludeFilters).IsEquivalentTo(["**/draft/**"]);
        await Assert.That(updated.SourceSyncedAt).IsEqualTo(existing.SourceSyncedAt);
        await Assert.That(updated.SyncStatus).IsEqualTo("idle");
    }

    // ─── Effective filter resolution ───────────────────────────────────

    [Test]
    public async Task EffectiveFilter_LibraryOverride_ReplacesSourceDefault()
    {
        // Guards that non-empty library filters replace source
        // defaults entirely, rather than merging with them.
        // The library says "I want only X", not "I want X in
        // addition to the source defaults."
        var library = LibraryBuilder
            .ForPublicSource("src")
            .WithIncludeFilter("lib-folder/**")
            .Build(NewId(), "org_a", "lib");

        var source = new DocsPublicSource
        {
            Id = "src",
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/repo",
            Name = "Example",
            DefaultIncludeFilters = ["source-default/**"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var effectiveIncludes =
            library.IncludeFilters.Length > 0
                ? library.IncludeFilters
                : source.DefaultIncludeFilters;

        await Assert.That(effectiveIncludes).IsEquivalentTo(["lib-folder/**"]);
    }

    [Test]
    public async Task EffectiveFilter_LibraryEmpty_FallsBackToSource()
    {
        // Guards that when the library has no include filters,
        // the source defaults are used. An empty array means
        // "no override" — not "include nothing."
        var library = LibraryBuilder.ForPublicSource("src").Build(NewId(), "org_a", "lib");

        var source = new DocsPublicSource
        {
            Id = "src",
            Kind = RepositorySourceKind.Public,
            RepoUrl = "https://github.com/example/repo",
            Name = "Example",
            DefaultIncludeFilters = ["source-default/**"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var effectiveIncludes =
            library.IncludeFilters.Length > 0
                ? library.IncludeFilters
                : source.DefaultIncludeFilters;

        await Assert.That(effectiveIncludes).IsEquivalentTo(["source-default/**"]);
    }
}
