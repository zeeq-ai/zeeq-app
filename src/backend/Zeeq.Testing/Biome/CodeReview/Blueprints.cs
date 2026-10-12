using Zeeq.Biome;
using Zeeq.Core.Models;
using Zeeq.Testing.Biomes;

namespace Zeeq.Testing.Biomes;

/// <summary>Code review construction preserving existing partition and numbering semantics.</summary>
[BiomeGraft]
public static class CodeReviewBlueprints
{
    /// <summary>Repository root scoped to the seed organization.</summary>
    [Graft("Repository", Plural = "Repositories")]
    public static readonly InputBlueprint<
        SeedContext,
        CodeRepositoryInput,
        CodeRepository
    > Repository = Blueprint.FromInput<SeedContext, CodeRepositoryInput, CodeRepository>(
        () => new(),
        (input, ctx) => CreateCodeRepository(ctx.Seed, input, ctx)
    );

    /// <summary>Lookup pointer child; does not insert a pull request record.</summary>
    [Graft("Lookup", Plural = "Lookups")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        CodeRepository,
        PullRequestLookupInput,
        PullRequestLookup
    > Lookup = Blueprint.ChildFromInput<
        SeedContext,
        CodeRepository,
        PullRequestLookupInput,
        PullRequestLookup
    >(() => new(), (parent, input, ctx) => CreatePullRequestLookup(ctx.Seed, parent, input, ctx));

    /// <summary>PR child with numbering reset at each Add call; explicit duplicates remain valid.</summary>
    [Graft("PullRequest", Plural = "PullRequests")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        CodeRepository,
        PullRequestRecordInput,
        PullRequestRecord
    > PullRequest = Blueprint.ChildFromInput<
        SeedContext,
        CodeRepository,
        PullRequestRecordInput,
        PullRequestRecord
    >(() => new(), (parent, input, ctx) => CreatePullRequestRecord(ctx.Seed, parent, input, ctx));

    /// <summary>Review child of a PR with repository ancestry.</summary>
    [Graft("Review", Plural = "Reviews")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        PullRequestRecord,
        CodeReviewRecordInput,
        CodeReviewRecord
    > Review = Blueprint.ChildFromInput<
        SeedContext,
        PullRequestRecord,
        CodeReviewRecordInput,
        CodeReviewRecord
    >(
        () => new(),
        (parent, input, ctx) =>
            CreateCodeReviewRecord(ctx.Seed, ctx.Ancestor<CodeRepository>(), parent, input, ctx)
    );

    /// <summary>Active guard anchored to a review, with its PR and repository ancestry.</summary>
    [Graft("ActiveLock", Plural = "ActiveLocks")]
    public static readonly ChildInputBlueprint<
        SeedContext,
        CodeReviewRecord,
        ActiveCodeReviewLockInput,
        ActiveCodeReviewLock
    > ActiveLock = Blueprint.ChildFromInput<
        SeedContext,
        CodeReviewRecord,
        ActiveCodeReviewLockInput,
        ActiveCodeReviewLock
    >(
        () => new(),
        (review, input, ctx) =>
        {
            var pr = ctx.Ancestor<PullRequestRecord>();
            var repository = ctx.Ancestor<CodeRepository>();
            return new()
            {
                OrganizationId = ctx.Seed.Organization.Id,
                RepositoryId = repository.Id,
                PullRequestRecordId = pr.Id,
                PullRequestCreatedAtUtc = pr.CreatedAtUtc,
                CodeReviewRecordId = review.Id,
                CodeReviewCreatedAtUtc = review.CreatedAtUtc,
                Status = input.Status,
                AcquiredAtUtc = review.CreatedAtUtc,
                ExpiresAtUtc = input.ExpiresAtUtc ?? ctx.FromNow(TimeSpan.FromMinutes(4)),
                UpdatedAtUtc = review.CreatedAtUtc,
            };
        }
    );

    /// <summary>Webhook delivery root; no repository columns or rows are implied.</summary>
    [Graft("Delivery", Plural = "Deliveries")]
    public static readonly InputBlueprint<
        SeedContext,
        GitHubWebhookDeliveryInput,
        GitHubWebhookDelivery
    > Delivery = Blueprint.FromInput<
        SeedContext,
        GitHubWebhookDeliveryInput,
        GitHubWebhookDelivery
    >(
        () => new(),
        (input, ctx) =>
            new() { DeliveryId = input.DeliveryId ?? ctx.NewId("delivery"), ClaimedAtUtc = ctx.Now }
    );

    private static CodeRepository CreateCodeRepository(
        SeedContext seed,
        CodeRepositoryInput prototype,
        BiomeContext<SeedContext> ctx
    )
    {
        var now = prototype.CreatedAtUtc ?? ctx.Now;
        var repositoryId = SeedContext.NewId("repo");
        var ownerQualifiedName = prototype.OwnerQualifiedName ?? $"zeeq-ai/{repositoryId}";

        return new()
        {
            Id = repositoryId,
            OrganizationId = seed.Organization.Id,
            TeamId = prototype.TeamId,
            Provider = prototype.Provider,
            OwnerQualifiedName = ownerQualifiedName,
            DisplayName = prototype.DisplayName ?? ownerQualifiedName,
            Enabled = prototype.Enabled,
            VisibleInLibraryPicker = prototype.VisibleInLibraryPicker,
            LibraryIds = [.. prototype.LibraryIds],
            // Clone to avoid sharing the singleton CodeRepositoryReviewConfiguration.Empty
            // instance across multiple repos in the same Biome session, which
            // confuses EF's owned-entity change tracker for the JSONB column.
            ReviewConfiguration = new CodeRepositoryReviewConfiguration
            {
                FileFilter = new CodeReviewFileFilter
                {
                    IncludedFiles = [.. prototype.ReviewConfiguration.FileFilter.IncludedFiles],
                    ExcludedFiles = [.. prototype.ReviewConfiguration.FileFilter.ExcludedFiles],
                },
                CheckRun = new CodeRepositoryReviewCheckRunConfiguration
                {
                    BlockOnCritical = prototype.ReviewConfiguration.CheckRun.BlockOnCritical,
                    BlockOnMajor = prototype.ReviewConfiguration.CheckRun.BlockOnMajor,
                },
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static PullRequestLookup CreatePullRequestLookup(
        SeedContext seed,
        CodeRepository repository,
        PullRequestLookupInput prototype,
        BiomeContext<SeedContext> ctx
    )
    {
        var createdAt = prototype.PullRequestCreatedAtUtc ?? ctx.Now;

        var number = prototype.PullRequestNumber ?? ctx.Index + 1;

        return new()
        {
            OrganizationId = seed.Organization.Id,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = number,
            PullRequestRecordId = prototype.PullRequestRecordId ?? SeedContext.NewId("pr"),
            PullRequestCreatedAtUtc = createdAt,
            UpdatedAtUtc = prototype.UpdatedAtUtc ?? createdAt,
        };
    }

    private static PullRequestRecord CreatePullRequestRecord(
        SeedContext seed,
        CodeRepository repository,
        PullRequestRecordInput prototype,
        BiomeContext<SeedContext> ctx
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? ctx.Now;

        var number = prototype.PullRequestNumber ?? ctx.Index + 1;

        return new()
        {
            Id = SeedContext.NewId("pr"),
            OrganizationId = seed.Organization.Id,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = number,
            GitHubNodeId = "PR_" + number,
            Title = prototype.Title ?? "Pull request " + number,
            Branch = prototype.Branch ?? "feature/" + number,
            BaseBranch = prototype.BaseBranch,
            HeadSha = Guid.CreateVersion7().ToString("N"),
            AuthorLogin = prototype.AuthorLogin,
            HtmlUrl = "https://github.com/" + repository.OwnerQualifiedName + "/pull/" + number,
            IsDraft = false,
            State = prototype.State,
            ClaimStatus = prototype.ClaimStatus,
            CreatedAtUtc = createdAt,
            CreatedFromWebhookAtUtc = createdAt,
            LastWebhookAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }

    private static CodeReviewRecord CreateCodeReviewRecord(
        SeedContext seed,
        CodeRepository repository,
        PullRequestRecord pullRequest,
        CodeReviewRecordInput prototype,
        BiomeContext<SeedContext> ctx
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? ctx.Now;

        return new()
        {
            Id = SeedContext.NewId("cr"),
            OrganizationId = seed.Organization.Id,
            PullRequestRecordId = pullRequest.Id,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = pullRequest.PullRequestNumber,
            Branch = pullRequest.Branch,
            Title = pullRequest.Title,
            AuthorLogin = pullRequest.AuthorLogin,
            Status = prototype.Status,
            RequestOrigin = prototype.RequestOrigin,
            RemainingReviewBudget = prototype.RemainingReviewBudget,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
    }
}
