using System.Collections;
using Zeeq.Core.Models;

namespace Zeeq.Testing.EntityGraphs;

/// <summary>
/// Test-builder prototype for repository mappings used by code-review tests.
/// </summary>
public sealed class CodeRepositoryPrototype
{
    /// <summary>
    /// Provider key such as <c>github</c>.
    /// </summary>
    public string Provider { get; set; } = "github";

    /// <summary>
    /// Provider-qualified repository name. A generated <c>owner/repo</c> name is used when omitted.
    /// </summary>
    public string? OwnerQualifiedName { get; set; }

    /// <summary>
    /// Human-readable display name. Defaults to <see cref="OwnerQualifiedName" />.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether the generated repository mapping is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the repository appears as a selectable private library source.
    /// </summary>
    public bool VisibleInLibraryPicker { get; set; } = true;

    /// <summary>
    /// Optional Zeeq team owner. Defaults to no team-level owner.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Typed repository review configuration.
    /// </summary>
    public CodeRepositoryReviewConfiguration ReviewConfiguration { get; set; } =
        CodeRepositoryReviewConfiguration.Empty;

    /// <summary>
    /// Library ids scoped to this repository for reviewer-agent tool access.
    /// </summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>
    /// Created timestamp for the generated repository row.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }
}

/// <summary>
/// Test-builder prototype for pull request lookup rows.
/// </summary>
public sealed class PullRequestLookupPrototype
{
    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int PullRequestNumber { get; set; } = 1;

    /// <summary>
    /// Partitioned pull request record ID currently targeted by the lookup.
    /// </summary>
    public string? PullRequestRecordId { get; set; }

    /// <summary>
    /// Created timestamp for the targeted partitioned pull request record.
    /// </summary>
    public DateTimeOffset? PullRequestCreatedAtUtc { get; set; }

    /// <summary>
    /// Lookup updated timestamp.
    /// </summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Whether the generated lookup row should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for partitioned pull request records.
/// </summary>
public sealed class PullRequestRecordPrototype
{
    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int PullRequestNumber { get; set; } = 1;

    /// <summary>
    /// Pull request created timestamp. This is also the partition key.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Pull request lifecycle state.
    /// </summary>
    public PullRequestState State { get; set; } = PullRequestState.Open;

    /// <summary>
    /// Zeeq review claim state for the pull request.
    /// </summary>
    public PullRequestClaimStatus ClaimStatus { get; set; } = PullRequestClaimStatus.Unclaimed;

    /// <summary>
    /// Pull request title. A generated title is used when omitted.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Pull request source branch. A generated branch is used when omitted.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Pull request target branch.
    /// </summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>
    /// Provider login for the pull request author.
    /// </summary>
    public string AuthorLogin { get; set; } = "octocat";

    /// <summary>
    /// Whether the generated pull request row should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for code review execution records.
/// </summary>
public sealed class CodeReviewRecordPrototype
{
    /// <summary>
    /// Pull request record to associate with the review. Defaults to the latest PR in the graph.
    /// </summary>
    public PullRequestRecord? PullRequest { get; set; }

    /// <summary>
    /// Review created timestamp. This is also the partition key.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Current review execution status.
    /// </summary>
    public CodeReviewStatus Status { get; set; } = CodeReviewStatus.Pending;

    /// <summary>
    /// Expiry for the generated active lock. Defaults to a live two-hour TTL.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Source that requested the review.
    /// </summary>
    public CodeReviewRequestOrigin RequestOrigin { get; set; } =
        CodeReviewRequestOrigin.RepositoryWebhook;

    /// <summary>
    /// Remaining budget for review attempts or retries.
    /// </summary>
    public int RemainingReviewBudget { get; set; } = 10;

    /// <summary>
    /// Whether the generated review row should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for active review guard rows.
/// </summary>
public sealed class ActiveCodeReviewLockPrototype
{
    /// <summary>
    /// Pull request record guarded by the lock. Defaults to the latest PR in the graph.
    /// </summary>
    public PullRequestRecord? PullRequest { get; set; }

    /// <summary>
    /// Code review record guarded by the lock. Defaults to the latest review in the graph.
    /// </summary>
    public CodeReviewRecord? CodeReview { get; set; }

    /// <summary>
    /// Active review status stored on the lock.
    /// </summary>
    public CodeReviewStatus Status { get; set; } = CodeReviewStatus.Pending;

    /// <summary>
    /// Expiry for the generated active lock. Defaults to a live two-hour TTL.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Whether the generated lock row should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for GitHub webhook delivery rows.
/// </summary>
public sealed class GitHubWebhookDeliveryPrototype
{
    /// <summary>
    /// GitHub delivery ID. A generated delivery ID is used when omitted.
    /// </summary>
    public string? DeliveryId { get; set; }

    /// <summary>
    /// Whether the generated delivery row should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Code-review-specific entity graph helpers.
/// </summary>
public static class EntityGraphCodeReviewExtensions
{
    extension<TState>(EntityGraphBuilder<TState> builder)
    {
        /// <summary>
        /// Adds one repository mapping for the seed organization.
        /// </summary>
        public EntityGraphBuilder<(TState Previous, CodeRepository Repository)> AddCodeRepository(
            Action<CodeRepositoryPrototype>? customize = null
        )
        {
            var prototype = new CodeRepositoryPrototype();
            customize?.Invoke(prototype);

            return builder.Add(seed => CreateCodeRepository(seed, prototype));
        }

        /// <summary>
        /// Adds pull request lookup rows tied to the latest repository mapping in the graph.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            PullRequestLookup[] Lookups
        )> AddPullRequestLookups(params Action<PullRequestLookupPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var repository = FindLatest<CodeRepository>(builder.Entities);
            var rows = new PullRequestLookup[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new PullRequestLookupPrototype { PullRequestNumber = index + 1 };
                customize[index].Invoke(prototype);

                var row = CreatePullRequestLookup(builder.Seed, repository, prototype);
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }

        /// <summary>
        /// Adds partitioned pull request records tied to the latest repository mapping in the graph.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            PullRequestRecord[] PullRequests
        )> AddPullRequestRecords(params Action<PullRequestRecordPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var repository = FindLatest<CodeRepository>(builder.Entities);
            var rows = new PullRequestRecord[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new PullRequestRecordPrototype { PullRequestNumber = index + 1 };
                customize[index].Invoke(prototype);

                var row = CreatePullRequestRecord(builder.Seed, repository, prototype);
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }

        /// <summary>
        /// Adds code review execution records tied to the latest repository and pull request in the graph.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            CodeReviewRecord[] CodeReviews
        )> AddCodeReviewRecords(params Action<CodeReviewRecordPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var repository = FindLatest<CodeRepository>(builder.Entities);
            var defaultPullRequest = FindLatest<PullRequestRecord>(builder.Entities);
            var rows = new CodeReviewRecord[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new CodeReviewRecordPrototype();
                customize[index].Invoke(prototype);

                var row = CreateCodeReviewRecord(
                    builder.Seed,
                    repository,
                    prototype.PullRequest ?? defaultPullRequest,
                    prototype
                );
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }

        /// <summary>
        /// Adds active code-review guard rows tied to the latest PR and review records in the graph.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            ActiveCodeReviewLock[] ActiveLocks
        )> AddActiveCodeReviewLocks(params Action<ActiveCodeReviewLockPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var repository = FindLatest<CodeRepository>(builder.Entities);
            var defaultPullRequest = FindLatest<PullRequestRecord>(builder.Entities);
            var defaultReview = FindLatest<CodeReviewRecord>(builder.Entities);
            var rows = new ActiveCodeReviewLock[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new ActiveCodeReviewLockPrototype();
                customize[index].Invoke(prototype);

                var row = CreateActiveCodeReviewLock(
                    builder.Seed,
                    repository,
                    prototype.PullRequest ?? defaultPullRequest,
                    prototype.CodeReview ?? defaultReview,
                    prototype
                );
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }

        /// <summary>
        /// Adds GitHub webhook delivery rows tied to the latest repository mapping in the graph.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            GitHubWebhookDelivery[] Deliveries
        )> AddGitHubWebhookDeliveries(params Action<GitHubWebhookDeliveryPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var repository = FindLatest<CodeRepository>(builder.Entities);
            var rows = new GitHubWebhookDelivery[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new GitHubWebhookDeliveryPrototype();
                customize[index].Invoke(prototype);

                var row = CreateGitHubWebhookDelivery(builder.Seed, repository, prototype);
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }
    }

    private static CodeRepository CreateCodeRepository(
        SeedContext seed,
        CodeRepositoryPrototype prototype
    )
    {
        var now = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;
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
            LibraryIds = prototype.LibraryIds,
            // Clone to avoid sharing the singleton CodeRepositoryReviewConfiguration.Empty
            // instance across multiple repos in the same EntityGraph chain, which
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
        PullRequestLookupPrototype prototype
    )
    {
        var createdAt = prototype.PullRequestCreatedAtUtc ?? DateTimeOffset.UtcNow;

        return new()
        {
            OrganizationId = seed.Organization.Id,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = prototype.PullRequestNumber,
            PullRequestRecordId = prototype.PullRequestRecordId ?? SeedContext.NewId("pr"),
            PullRequestCreatedAtUtc = createdAt,
            UpdatedAtUtc = prototype.UpdatedAtUtc ?? createdAt,
        };
    }

    private static PullRequestRecord CreatePullRequestRecord(
        SeedContext seed,
        CodeRepository repository,
        PullRequestRecordPrototype prototype
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;

        return new()
        {
            Id = SeedContext.NewId("pr"),
            OrganizationId = seed.Organization.Id,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = prototype.PullRequestNumber,
            GitHubNodeId = "PR_" + prototype.PullRequestNumber,
            Title = prototype.Title ?? "Pull request " + prototype.PullRequestNumber,
            Branch = prototype.Branch ?? "feature/" + prototype.PullRequestNumber,
            BaseBranch = prototype.BaseBranch,
            HeadSha = Guid.CreateVersion7().ToString("N"),
            AuthorLogin = prototype.AuthorLogin,
            HtmlUrl =
                "https://github.com/"
                + repository.OwnerQualifiedName
                + "/pull/"
                + prototype.PullRequestNumber,
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
        CodeReviewRecordPrototype prototype
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;

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

    private static ActiveCodeReviewLock CreateActiveCodeReviewLock(
        SeedContext seed,
        CodeRepository repository,
        PullRequestRecord pullRequest,
        CodeReviewRecord review,
        ActiveCodeReviewLockPrototype prototype
    ) =>
        new()
        {
            OrganizationId = seed.Organization.Id,
            RepositoryId = repository.Id,
            PullRequestRecordId = pullRequest.Id,
            PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
            CodeReviewRecordId = review.Id,
            CodeReviewCreatedAtUtc = review.CreatedAtUtc,
            Status = prototype.Status,
            AcquiredAtUtc = review.CreatedAtUtc,
            ExpiresAtUtc = prototype.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(4),
            UpdatedAtUtc = review.CreatedAtUtc,
        };

    private static GitHubWebhookDelivery CreateGitHubWebhookDelivery(
        SeedContext seed,
        CodeRepository repository,
        GitHubWebhookDeliveryPrototype prototype
    ) =>
        new()
        {
            DeliveryId = prototype.DeliveryId ?? SeedContext.NewId("delivery"),
            ClaimedAtUtc = DateTimeOffset.UtcNow,
        };

    private static T FindLatest<T>(IReadOnlyList<object> entities)
    {
        foreach (var entity in Flatten(entities).Reverse())
        {
            if (entity is T typed)
            {
                return typed;
            }
        }

        throw new InvalidOperationException(
            $"EntityGraph requires an entity of type {typeof(T).Name} earlier in the graph."
        );
    }

    private static IEnumerable<object> Flatten(IEnumerable<object> entities)
    {
        foreach (var entity in entities)
        {
            if (entity is string)
            {
                yield return entity;
                continue;
            }

            if (entity is IEnumerable enumerable)
            {
                foreach (var item in enumerable.OfType<object>())
                {
                    foreach (var nested in Flatten([item]))
                    {
                        yield return nested;
                    }
                }

                continue;
            }

            yield return entity;
        }
    }
}
