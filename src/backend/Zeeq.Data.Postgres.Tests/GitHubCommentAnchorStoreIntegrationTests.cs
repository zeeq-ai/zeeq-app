using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for durable GitHub comment anchor storage.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubCommentAnchorStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class GitHubCommentAnchorStoreIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private readonly PgDatabaseFixture _postgres = postgres;

    [Test]
    public async Task FindAsync_ReturnsNullWhenAnchorDoesNotExist()
    {
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var target = NewTarget(repository);
        var store = new PostgresGitHubCommentAnchorStore(_context);

        var found = await store.FindAsync(target, CancellationToken.None);

        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task UpsertResolvedAsync_CreatesAnchorForTarget()
    {
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var target = NewTarget(repository);
        var store = new PostgresGitHubCommentAnchorStore(_context);

        var anchor = await store.UpsertResolvedAsync(
            target,
            repository.OwnerQualifiedName,
            gitHubCommentId: 10_001,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var found = await store.FindAsync(target, CancellationToken.None);

        await Assert.That(anchor.TargetKey).IsEqualTo(target.ToStorageKey());
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.GitHubCommentId).IsEqualTo(10_001);
        await Assert.That(found.OwnerQualifiedRepoName).IsEqualTo(repository.OwnerQualifiedName);
        await Assert.That(found.Kind).IsEqualTo(GitHubCommentTargetKind.PullRequestSummary);
        await Assert.That(found.LastResolvedAtUtc).IsNotNull();
    }

    [Test]
    public async Task UpsertResolvedAsync_RepairsStoredCommentId()
    {
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var target = NewTarget(repository);
        var store = new PostgresGitHubCommentAnchorStore(_context);
        await store.UpsertResolvedAsync(
            target,
            repository.OwnerQualifiedName,
            gitHubCommentId: 10_001,
            CancellationToken.None
        );
        var firstResolvedAt = (
            await store.FindAsync(target, CancellationToken.None)
        )!.LastResolvedAtUtc;

        var repaired = await store.UpsertResolvedAsync(
            target,
            repository.OwnerQualifiedName,
            gitHubCommentId: 10_002,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var found = await store.FindAsync(target, CancellationToken.None);

        await Assert.That(repaired.TargetKey).IsEqualTo(target.ToStorageKey());
        await Assert.That(found!.GitHubCommentId).IsEqualTo(10_002);
        await Assert.That(found.LastResolvedAtUtc).IsNotNull();
        await Assert.That(firstResolvedAt).IsNotNull();
        await Assert
            .That(found.LastResolvedAtUtc!.Value)
            .IsGreaterThanOrEqualTo(firstResolvedAt!.Value);
    }

    [Test]
    public async Task AnchorTable_EnforcesOneRowPerLogicalTarget()
    {
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var target = NewTarget(repository);
        var first = new GitHubCommentAnchor
        {
            TargetKey = SeedContext.NewId("anchor"),
            OrganizationId = target.OrganizationId,
            RepositoryId = target.RepositoryId,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = target.PullRequestNumber,
            Kind = target.Kind,
            ScopeKey = target.ScopeKey,
            GitHubCommentId = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var duplicateTarget = new GitHubCommentAnchor
        {
            TargetKey = SeedContext.NewId("anchor"),
            OrganizationId = target.OrganizationId,
            RepositoryId = target.RepositoryId,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = target.PullRequestNumber,
            Kind = target.Kind,
            ScopeKey = target.ScopeKey,
            GitHubCommentId = 2,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        _context.GitHubCommentAnchors.Add(first);
        await _context.SaveChangesAsync();
        _context.GitHubCommentAnchors.Add(duplicateTarget);

        await Assert
            .That(async () => await _context.SaveChangesAsync())
            .Throws<DbUpdateException>();
    }

    [Test]
    public async Task UpsertResolvedAsync_IsIdempotentWhenConcurrentWritersCreateSameTarget()
    {
        await using var seedContext = _postgres.CreateContext();
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(seedContext)
            .AddCodeRepository()
            .BuildAsync();
        var target = NewTarget(repository);

        await using var firstContext = _postgres.CreateContext();
        await using var secondContext = _postgres.CreateContext();
        var firstStore = new PostgresGitHubCommentAnchorStore(firstContext);
        var secondStore = new PostgresGitHubCommentAnchorStore(secondContext);

        await Task.WhenAll(
            firstStore.UpsertResolvedAsync(
                target,
                repository.OwnerQualifiedName,
                gitHubCommentId: 10_101,
                CancellationToken.None
            ),
            secondStore.UpsertResolvedAsync(
                target,
                repository.OwnerQualifiedName,
                gitHubCommentId: 10_102,
                CancellationToken.None
            )
        );

        try
        {
            await using var assertionContext = _postgres.CreateContext();
            var anchors = await assertionContext
                .GitHubCommentAnchors.AsNoTracking()
                .Where(anchor => anchor.TargetKey == target.ToStorageKey())
                .ToListAsync();

            await Assert.That(anchors).Count().IsEqualTo(1);
            await Assert.That(anchors[0].GitHubCommentId).IsIn([10_101L, 10_102L]);
        }
        finally
        {
            // This test intentionally commits through separate contexts so the
            // unique-key race is real. Clean up the unique target explicitly.
            await using var cleanupContext = _postgres.CreateContext();
            await cleanupContext
                .GitHubCommentAnchors.Where(anchor => anchor.TargetKey == target.ToStorageKey())
                .ExecuteDeleteAsync();
        }
    }

    private static GitHubCommentTargetSelector NewTarget(CodeRepository repository) =>
        new(
            OrganizationId: repository.OrganizationId,
            RepositoryId: repository.Id,
            PullRequestNumber: Random.Shared.Next(1, 10_000),
            Kind: GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey: "root"
        );
}
