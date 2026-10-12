using Zeeq.Biome;
using Zeeq.Core.Documents;
using Zeeq.Testing.Biomes;

namespace Zeeq.Testing.Biomes;

/// <summary>Docs runs retain explicit library identifiers without creating library rows.</summary>
[BiomeGraft]
public static class DocsIngestBlueprints
{
    /// <summary>Public source root.</summary>
    [Graft("PublicSource", Plural = "PublicSources")]
    public static readonly InputBlueprint<
        SeedContext,
        DocsPublicSourceInput,
        DocsPublicSource
    > PublicSource = Blueprint.FromInput<SeedContext, DocsPublicSourceInput, DocsPublicSource>(
        () => new(),
        (input, ctx) => CreateDocsPublicSource(ctx.Seed, input, ctx)
    );

    /// <summary>Standalone run with the existing private default.</summary>
    [Graft("Run", Plural = "Runs")]
    public static readonly InputBlueprint<SeedContext, DocsIngestRunInput, DocsIngestRun> Run =
        Blueprint.FromInput<SeedContext, DocsIngestRunInput, DocsIngestRun>(
            () => new(),
            (input, ctx) => CreateDocsIngestRun(ctx.Seed, input, ctx)
        );

    /// <summary>Public run child; the source is an owned output anchor.</summary>
    [Graft("PublicRun", Plural = "PublicRuns")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        DocsPublicSource,
        DocsIngestRunInput,
        DocsIngestRun
    > PublicRun = Blueprint.ChildFromInput<
        SeedContext,
        DocsPublicSource,
        DocsIngestRunInput,
        DocsIngestRun
    >(
        () => new() { SourceKind = RepositorySourceKind.Public },
        (parent, input, ctx) =>
        {
            if (input.SourceKind != RepositorySourceKind.Public)
            {
                throw new ArgumentException(
                    "A public child run must have Public source kind.",
                    nameof(input)
                );
            }

            return CreateDocsIngestRun(ctx.Seed, input, ctx, parent);
        }
    );

    /// <summary>Document child of an explicitly anchored source.</summary>
    [Graft("PublicDocument", Plural = "PublicDocuments")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        DocsPublicSource,
        DocsPublicDocumentInput,
        DocsPublicDocument
    > PublicDocument = Blueprint.ChildFromInput<
        SeedContext,
        DocsPublicSource,
        DocsPublicDocumentInput,
        DocsPublicDocument
    >(
        () => new(),
        (source, input, ctx) =>
        {
            var now = ctx.Now;
            var title = input.Title ?? "Test Document";
            return new()
            {
                Id = ctx.NewId("doc"),
                PublicSourceId = source.Id,
                Path = input.Path ?? $"/guides/{ctx.NewId("doc")[5..]}.md",
                Title = title,
                TitleNormalized = title.ToLowerInvariant(),
                Content = input.Content,
                ContentHash = input.ContentHash ?? ctx.NewId("hash"),
                SyncRunId = input.SyncRunId,
                PreviousPaths = [.. input.PreviousPaths],
                CreatedAt = ctx.Now,
                UpdatedAt = ctx.Now,
            };
        }
    );

    private static DocsPublicSource CreateDocsPublicSource(
        SeedContext seed,
        DocsPublicSourceInput prototype,
        BiomeContext<SeedContext> ctx
    )
    {
        var now = ctx.Now;

        return new()
        {
            Id = prototype.Id ?? SeedContext.NewId("src"),
            Kind = RepositorySourceKind.Public,
            RepoUrl =
                prototype.RepoUrl
                ?? $"https://github.com/example/repo-{SeedContext.NewId("repo")[5..]}",
            // NOTE: The original source prototype has no Name input; retain its fixed name.
            Name = "Test Source",
            DefaultIncludeFilters = [.. prototype.DefaultIncludeFilters],
            DefaultExcludeFilters = [.. prototype.DefaultExcludeFilters],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static DocsIngestRun CreateDocsIngestRun(
        SeedContext seed,
        DocsIngestRunInput prototype,
        BiomeContext<SeedContext> ctx,
        DocsPublicSource? publicSource = null
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? ctx.Now;

        return new()
        {
            Id = prototype.Id ?? SeedContext.NewId("run"),
            CreatedAtUtc = createdAt,
            SourceKind = prototype.SourceKind,
            RepoUrl = prototype.RepoUrl ?? "https://github.com/acme/repo",
            PublicSourceId =
                prototype.SourceKind == RepositorySourceKind.Public
                    ? prototype.PublicSourceId ?? publicSource?.Id ?? SeedContext.NewId("src")
                    : null,
            OrganizationId = prototype.OrganizationId ?? seed.Organization.Id,
            LibraryId = prototype.LibraryId ?? SeedContext.NewId("lib"),
            Trigger = prototype.Trigger,
            Status = prototype.Status,
            FilesTotal = prototype.FilesTotal,
            FilesAdded = prototype.FilesAdded,
            FilesUpdated = prototype.FilesUpdated,
            FilesMoved = prototype.FilesMoved,
            FilesSkipped = prototype.FilesSkipped,
            FilesDeleted = prototype.FilesDeleted,
            FilesFailed = prototype.FilesFailed,
            AuthFailure = prototype.AuthFailure,
            FailureMessage = prototype.FailureMessage,
            StartedAtUtc = prototype.StartedAtUtc ?? createdAt,
            CompletedAtUtc = prototype.CompletedAtUtc,
            UpdatedAtUtc = createdAt,
        };
    }
}
