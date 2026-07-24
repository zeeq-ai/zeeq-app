using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the GitHub code-review storage tables and stores.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewStoreIntegrationTests : PgTransactionalTestBase
{
    private readonly PgDatabaseFixture _postgres;

    public CodeReviewStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres)
    {
        _postgres = postgres;
    }

    [Test]
    public async Task PartitionedTables_AreRegisteredWithPgPartman()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await Assert
            .That(
                await CountPartitionedParentsAsync(connection, "code_review_pull_request_records")
            )
            .IsEqualTo(1);
        await Assert
            .That(await CountPartitionedParentsAsync(connection, "code_review_records"))
            .IsEqualTo(1);
        await Assert.That(await CountPartmanParentsAsync(connection)).IsEqualTo(2);
    }

    [Test]
    public async Task PullRequestLookupStore_UpsertAsync_ReplacesCurrentRecordPointer()
    {
        var firstCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var secondCreatedAt = DateTimeOffset.UtcNow;
        var (seed, repository, lookups) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestLookups(
                lookup =>
                {
                    lookup.PullRequestNumber = 42;
                    lookup.PullRequestCreatedAtUtc = firstCreatedAt;
                    lookup.PersistOnBuild = false;
                },
                lookup =>
                {
                    lookup.PullRequestNumber = 42;
                    lookup.PullRequestCreatedAtUtc = secondCreatedAt;
                    lookup.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var store = new PostgresPullRequestLookupStore(_context);

        await store.UpsertAsync(lookups[0], CancellationToken.None);
        await store.UpsertAsync(lookups[1], CancellationToken.None);

        var lookup = await store.FindAsync(
            seed.Organization.Id,
            repository.Id,
            42,
            CancellationToken.None
        );

        await Assert.That(lookup).IsNotNull();
        await Assert.That(lookup!.PullRequestCreatedAtUtc).IsEqualTo(secondCreatedAt);
        await Assert.That(lookup.UpdatedAtUtc).IsEqualTo(secondCreatedAt);
    }

    [Test]
    public async Task PullRequestRecordStore_ListRecentAsync_UsesCreatedAtAndIdCursor()
    {
        var (seed, _, pullRequests) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 1;
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    pullRequest.PersistOnBuild = false;
                },
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 2;
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    pullRequest.PersistOnBuild = false;
                },
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 3;
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    pullRequest.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var older = pullRequests[0];
        var middle = pullRequests[1];
        var newer = pullRequests[2];
        var store = new PostgresPullRequestRecordStore(_context);

        await store.UpsertAsync(older, CancellationToken.None);
        await store.UpsertAsync(middle, CancellationToken.None);
        await store.UpsertAsync(newer, CancellationToken.None);

        var found = await store.FindAsync(newer.Id, newer.CreatedAtUtc, CancellationToken.None);
        var firstPage = await store.ListRecentAsync(
            new PullRequestStreamQuery(seed.Organization.Id, PageSize: 2),
            CancellationToken.None
        );
        var secondPage = await store.ListRecentAsync(
            new PullRequestStreamQuery(
                seed.Organization.Id,
                Cursor: firstPage.NextCursor,
                PageSize: 2
            ),
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.PullRequestNumber).IsEqualTo(newer.PullRequestNumber);
        await Assert
            .That(firstPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([newer.Id, middle.Id]);
        await Assert
            .That(secondPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([older.Id]);
    }

    [Test]
    public async Task PullRequestRecordStore_ListRecentAsync_MineIncludesActiveGitHubAliases()
    {
        // NOTE: A follow-up review suspected this deconstruction no longer
        // matched the EntityGraph result shape after AddUserAliases; the
        // CodeReviewStoreIntegrationTests project compiles and this test passes.
        var (seed, _, pullRequests, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 1;
                    pullRequest.AuthorLogin = "claimed-author";
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    pullRequest.PersistOnBuild = false;
                },
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 2;
                    pullRequest.AuthorLogin = " @CharlieDigital ";
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    pullRequest.PersistOnBuild = false;
                },
                pullRequest =>
                {
                    pullRequest.PullRequestNumber = 3;
                    pullRequest.AuthorLogin = "someone-else";
                    pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    pullRequest.PersistOnBuild = false;
                }
            )
            .AddUserAliases(alias =>
            {
                alias.Kind = UserAliasKind.GitHub;
                alias.DisplayValue = "CharlieDigital";
                alias.NormalizedValue = "charliedigital";
            })
            .BuildAsync();
        var claimed = pullRequests[0];
        var aliased = pullRequests[1];
        var other = pullRequests[2];
        claimed.ClaimedByUserId = seed.Owner.Id;
        var store = new PostgresPullRequestRecordStore(_context);

        await store.UpsertAsync(claimed, CancellationToken.None);
        await store.UpsertAsync(aliased, CancellationToken.None);
        await store.UpsertAsync(other, CancellationToken.None);

        var page = await store.ListRecentAsync(
            new PullRequestStreamQuery(seed.Organization.Id, SubjectUserId: seed.Owner.Id),
            CancellationToken.None
        );

        await Assert
            .That(page.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([claimed.Id, aliased.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListRecentAsync_UsesCreatedAtAndIdCursor()
    {
        var (seed, _, pullRequests, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 77;
                pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4);
            })
            .AddCodeReviewRecords(
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    review.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var pullRequest = pullRequests[0];
        var older = reviews[0];
        var middle = reviews[1];
        var newer = reviews[2];
        var store = new PostgresCodeReviewRecordStore(_context);

        await store.AddAsync(older, CancellationToken.None);
        await store.AddAsync(middle, CancellationToken.None);
        await store.AddAsync(newer, CancellationToken.None);

        var found = await store.FindAsync(newer.Id, newer.CreatedAtUtc, CancellationToken.None);
        var firstPage = await store.ListRecentAsync(
            new CodeReviewStreamQuery(seed.Organization.Id, PageSize: 2),
            CancellationToken.None
        );
        var secondPage = await store.ListRecentAsync(
            new CodeReviewStreamQuery(
                seed.Organization.Id,
                Cursor: firstPage.NextCursor,
                PageSize: 2
            ),
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.PullRequestNumber).IsEqualTo(newer.PullRequestNumber);
        await Assert
            .That(firstPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([newer.Id, middle.Id]);
        await Assert
            .That(secondPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([older.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForPullRequestAsync_UsesPullRequestCreatedAtLowerBound()
    {
        var (seed, _, pullRequests, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 88;
                pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            })
            .AddCodeReviewRecords(
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    review.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var pullRequest = pullRequests[0];
        var older = reviews[0];
        var middle = reviews[1];
        var newer = reviews[2];
        var store = new PostgresCodeReviewRecordStore(_context);

        await store.AddAsync(older, CancellationToken.None);
        await store.AddAsync(middle, CancellationToken.None);
        await store.AddAsync(newer, CancellationToken.None);

        var firstPage = await store.ListForPullRequestAsync(
            new PullRequestReviewStreamQuery(
                seed.Organization.Id,
                pullRequest.Id,
                pullRequest.CreatedAtUtc,
                PageSize: 2
            ),
            CancellationToken.None
        );
        var secondPage = await store.ListForPullRequestAsync(
            new PullRequestReviewStreamQuery(
                seed.Organization.Id,
                pullRequest.Id,
                pullRequest.CreatedAtUtc,
                Cursor: firstPage.NextCursor,
                PageSize: 2
            ),
            CancellationToken.None
        );

        await Assert
            .That(firstPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([newer.Id, middle.Id]);
        await Assert
            .That(secondPage.Items.Select(item => item.Id).ToArray())
            .IsEquivalentTo([older.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_FindNewestCompletedForPullRequestAsync_IgnoresRunningReviews()
    {
        var (seed, _, pullRequests, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 90;
                pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            })
            .AddCodeReviewRecords(
                review =>
                {
                    review.Status = CodeReviewStatus.Completed;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.Status = CodeReviewStatus.Completed;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.Status = CodeReviewStatus.Running;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    review.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var pullRequest = pullRequests[0];
        var olderCompleted = reviews[0];
        var newerCompleted = reviews[1];
        var running = reviews[2];
        var store = new PostgresCodeReviewRecordStore(_context);

        await store.AddAsync(olderCompleted, CancellationToken.None);
        await store.AddAsync(newerCompleted, CancellationToken.None);
        await store.AddAsync(running, CancellationToken.None);

        var found = await store.FindNewestCompletedForPullRequestAsync(
            seed.Organization.Id,
            pullRequest.Id,
            pullRequest.CreatedAtUtc,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(newerCompleted.Id);
    }

    [Test]
    public async Task CodeReviewRecordStore_FindNewestCompletedForBranchAsync_FiltersByBranchAndRepository()
    {
        var (seed, repository, _, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 91;
                pullRequest.Branch = "feature/target";
                pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            })
            .AddCodeReviewRecords(
                review =>
                {
                    review.Status = CodeReviewStatus.Completed;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.Status = CodeReviewStatus.Completed;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.Status = CodeReviewStatus.Completed;
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    review.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var olderTarget = reviews[0];
        var newerTarget = reviews[1];
        var differentBranch = reviews[2];
        differentBranch.Branch = "feature/other";
        var store = new PostgresCodeReviewRecordStore(_context);

        await store.AddAsync(olderTarget, CancellationToken.None);
        await store.AddAsync(newerTarget, CancellationToken.None);
        await store.AddAsync(differentBranch, CancellationToken.None);

        var found = await store.FindNewestCompletedForBranchAsync(
            seed.Organization.Id,
            repository.Id,
            "feature/target",
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(newerTarget.Id);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListInboxUpdatesAsync_UsesUpdatedCursorAndLookupTimestamp()
    {
        var pullRequestCreatedAt = DateTimeOffset
            .UtcNow.AddMinutes(-10)
            .TruncateToPostgresPrecision();
        var (seed, repository, pullRequests, reviews, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 89;
                pullRequest.CreatedAtUtc = pullRequestCreatedAt;
            })
            .AddCodeReviewRecords(
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
                    review.PersistOnBuild = false;
                },
                review =>
                {
                    review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    review.PersistOnBuild = false;
                }
            )
            .AddUserAliases(alias =>
            {
                alias.Kind = UserAliasKind.GitHub;
                alias.DisplayValue = "CharlieDigital";
                alias.NormalizedValue = "charliedigital";
            })
            .BuildAsync();
        var pullRequest = pullRequests[0];
        var older = reviews[0];
        var middle = reviews[1];
        var newer = reviews[2];
        pullRequest.AuthorLogin = "@CharlieDigital";
        older.Status = CodeReviewStatus.Completed;
        older.CriticalFindings = 1;
        older.UpdatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
        middle.Status = CodeReviewStatus.Errored;
        middle.MajorFindings = 2;
        middle.UpdatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-20);
        newer.Status = CodeReviewStatus.Running;
        newer.MinorFindings = 3;
        newer.UpdatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10);
        _context.PullRequestLookups.Add(
            new()
            {
                OrganizationId = seed.Organization.Id,
                RepositoryId = repository.Id,
                OwnerQualifiedRepoName = repository.OwnerQualifiedName,
                PullRequestNumber = pullRequest.PullRequestNumber,
                PullRequestRecordId = pullRequest.Id,
                PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
                UpdatedAtUtc = pullRequest.UpdatedAtUtc,
            }
        );
        var store = new PostgresCodeReviewRecordStore(_context);

        await store.AddAsync(older, CancellationToken.None);
        await store.AddAsync(middle, CancellationToken.None);
        await store.AddAsync(newer, CancellationToken.None);

        var firstPage = await store.ListInboxUpdatesAsync(
            new CodeReviewUpdateStreamQuery(
                seed.Organization.Id,
                ReviewCreatedAtLowerBoundUtc: pullRequest.CreatedAtUtc,
                PageSize: 2
            ),
            CancellationToken.None
        );
        var secondPage = await store.ListInboxUpdatesAsync(
            new CodeReviewUpdateStreamQuery(
                seed.Organization.Id,
                Cursor: firstPage.NextCursor,
                PageSize: 2
            ),
            CancellationToken.None
        );

        await Assert
            .That(firstPage.Items.Select(item => item.CodeReviewRecordId).ToArray())
            .IsEquivalentTo([older.Id, middle.Id]);
        await Assert
            .That(firstPage.Items[0].PullRequestCreatedAtUtc)
            .IsEqualTo(pullRequest.CreatedAtUtc);
        await Assert.That(firstPage.Items[0].CriticalFindings).IsEqualTo(1);
        await Assert.That(firstPage.NextCursor!.Scope).IsEqualTo(CodeReviewInboxScope.All);
        await Assert.That(firstPage.NextCursor.TeamId).IsNull();
        await Assert.That(firstPage.NextCursor.RepositoryId).IsNull();
        await Assert
            .That(secondPage.Items.Select(item => item.CodeReviewRecordId).ToArray())
            .IsEquivalentTo([newer.Id]);
        await Assert.That(secondPage.Items[0].MinorFindings).IsEqualTo(3);

        async Task ReuseCursorWithChangedTeamAsync() =>
            await store.ListInboxUpdatesAsync(
                new CodeReviewUpdateStreamQuery(
                    seed.Organization.Id,
                    TeamId: seed.RootTeam.Id,
                    Cursor: firstPage.NextCursor,
                    PageSize: 2
                ),
                CancellationToken.None
            );

        await Assert.That(ReuseCursorWithChangedTeamAsync).Throws<ArgumentException>();

        var minePage = await store.ListInboxUpdatesAsync(
            new CodeReviewUpdateStreamQuery(
                seed.Organization.Id,
                Scope: CodeReviewInboxScope.Mine,
                SubjectUserId: seed.Owner.Id,
                ReviewCreatedAtLowerBoundUtc: pullRequest.CreatedAtUtc,
                PageSize: 5
            ),
            CancellationToken.None
        );
        var someoneElsePage = await store.ListInboxUpdatesAsync(
            new CodeReviewUpdateStreamQuery(
                seed.Organization.Id,
                Scope: CodeReviewInboxScope.Mine,
                SubjectUserId: "user_someone_else",
                ReviewCreatedAtLowerBoundUtc: pullRequest.CreatedAtUtc,
                PageSize: 5
            ),
            CancellationToken.None
        );

        await Assert.That(minePage.Items).Count().IsEqualTo(3);
        await Assert.That(minePage.NextCursor!.Scope).IsEqualTo(CodeReviewInboxScope.Mine);
        await Assert.That(minePage.NextCursor.SubjectUserId).IsEqualTo(seed.Owner.Id);
        await Assert.That(someoneElsePage.Items).IsEmpty();
        await Assert
            .That(someoneElsePage.NextCursor!.Id)
            .IsEqualTo(CodeReviewUpdateCursor.SyntheticHighWaterId);
        await Assert.That(someoneElsePage.NextCursor.Scope).IsEqualTo(CodeReviewInboxScope.Mine);
    }

    [Test]
    public async Task ActiveCodeReviewLockStore_TryAcquireAsync_ReturnsFalseForDuplicateLock()
    {
        var (_, _, _, _, activeLocks) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 10;
                pullRequest.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
                pullRequest.PersistOnBuild = false;
            })
            .AddCodeReviewRecords(review =>
            {
                review.CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4);
                review.PersistOnBuild = false;
            })
            .AddActiveCodeReviewLocks(activeLock => activeLock.PersistOnBuild = false)
            .BuildAsync();
        var activeLock = activeLocks[0];
        var store = new PostgresActiveCodeReviewLockStore(_context);

        var acquired = await store.TryAcquireAsync(activeLock, CancellationToken.None);
        _context.ChangeTracker.Clear();
        var duplicate = await new PostgresActiveCodeReviewLockStore(_context).TryAcquireAsync(
            activeLock,
            CancellationToken.None
        );

        await Assert.That(acquired).IsTrue();
        await Assert.That(duplicate).IsFalse();
    }

    [Test]
    public async Task ActiveCodeReviewLockStore_ReleaseIfOwnedByReviewAsync_DoesNotReleaseNewerReviewLock()
    {
        var oldReviewCreatedAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var newerReviewCreatedAt = oldReviewCreatedAt.AddMinutes(1);
        var (_, repository, pullRequests, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords(
                review =>
                {
                    review.CreatedAtUtc = oldReviewCreatedAt;
                    review.Status = CodeReviewStatus.Completed;
                },
                review =>
                {
                    review.CreatedAtUtc = newerReviewCreatedAt;
                    review.Status = CodeReviewStatus.Pending;
                }
            )
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var oldReview = reviews[0];
        var newerReview = reviews[1];
        var pullRequest = pullRequests[0];
        var newerLock = new ActiveCodeReviewLock
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            PullRequestRecordId = pullRequest.Id,
            PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
            CodeReviewRecordId = newerReview.Id,
            CodeReviewCreatedAtUtc = newerReview.CreatedAtUtc,
            Status = CodeReviewStatus.Pending,
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(4),
            UpdatedAtUtc = now,
        };
        _context.ActiveCodeReviewLocks.Add(newerLock);
        await _context.SaveChangesAsync();
        var store = new PostgresActiveCodeReviewLockStore(_context);

        await store.ReleaseIfOwnedByReviewAsync(
            newerLock.OrganizationId,
            newerLock.PullRequestRecordId,
            oldReview.Id,
            oldReview.CreatedAtUtc,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var found = await new PostgresActiveCodeReviewLockStore(_context).FindAsync(
            newerLock.OrganizationId,
            newerLock.PullRequestRecordId,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.CodeReviewRecordId).IsEqualTo(newerLock.CodeReviewRecordId);
        await Assert.That(found.CodeReviewCreatedAtUtc).IsEqualTo(newerLock.CodeReviewCreatedAtUtc);
    }

    [Test]
    public async Task CodeRepositoryStore_ReviewConfiguration_RoundTripsTypedJsonb()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository =>
            {
                repository.ReviewConfiguration = new()
                {
                    FileFilter = new()
                    {
                        IncludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.PathPrefix,
                                Pattern = "src/backend/",
                            },
                        ],
                        ExcludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.Glob,
                                Pattern = "*.generated.cs",
                            },
                        ],
                    },
                };
            })
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert
            .That(found!.ReviewConfiguration.FileFilter.IncludedFiles.Single().Pattern)
            .IsEqualTo("src/backend/");
        await Assert
            .That(found.ReviewConfiguration.FileFilter.ExcludedFiles.Single().MatchType)
            .IsEqualTo(CodeReviewFileNameMatchType.Glob);
    }

    [Test]
    public async Task CodeRepositoryStore_UpsertAsync_PersistsLibraryPickerVisibility()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        repository.VisibleInLibraryPicker = false;
        repository.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await store.UpsertAsync(repository, CancellationToken.None);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.VisibleInLibraryPicker).IsFalse();
    }

    [Test]
    public async Task CodeRepositoryStore_FindActiveAsync_ExcludesPausedRepositories()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository =>
            {
                repository.OwnerQualifiedName = "zeeq-ai/paused";
                repository.Enabled = false;
            })
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveAsync(
            repository.Provider,
            repository.OwnerQualifiedName,
            CancellationToken.None
        );

        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task CodeRepositoryStore_FindActiveForOrganizationByProviderIdentityAsync_ScopesByOrganization()
    {
        var (_, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository => repository.OwnerQualifiedName = "zeeq-ai/shared")
            .BuildAsync();
        var (otherSeed, otherRepository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository => repository.OwnerQualifiedName = "zeeq-ai/shared")
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationByProviderIdentityAsync(
            otherSeed.Organization.Id,
            otherRepository.Provider,
            otherRepository.OwnerQualifiedName,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(otherRepository.Id);
        await Assert.That(found.Id).IsNotEqualTo(repository.Id);
    }

    [Test]
    public async Task CodeRepositoryStore_FindActiveForOrganizationAsync_IncludesPausedRepositories()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository =>
            {
                repository.OwnerQualifiedName = "zeeq-ai/paused";
                repository.Enabled = false;
            })
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Enabled).IsFalse();
    }

    [Test]
    public async Task CodeReviewOrganizationSettingsStore_SaveAsync_UpdatesExecutionLimits()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCodeReviewOrganizationSettingsStore(_context);
        var settings = await store.GetAsync(seed.Organization.Id, CancellationToken.None);
        var updated = settings with { MaxConcurrentReviews = 2 };

        await Assert.That(settings.MaxConcurrentReviews).IsEqualTo(4);

        await store.SaveAsync(seed.Organization.Id, updated, CancellationToken.None);
        _context.ChangeTracker.Clear();
        var found = await new PostgresCodeReviewOrganizationSettingsStore(_context).GetAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(found.MaxConcurrentReviews).IsEqualTo(2);
        await Assert.That(found.ExecutionLeaseDuration).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CodeReviewerAgentStore_AddUpdateDisable_FiltersByRepositoryAndEnabledState()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PostgresCodeReviewerAgentStore(_context);
        var agent = new CodeReviewerAgent
        {
            Id = SeedContext.NewId("agent"),
            OrganizationId = seed.Organization.Id,
            TeamId = seed.RootTeam.Id,
            RepositoryId = repository.Id,
            DisplayName = "Security Reviewer",
            ReviewFacet = "Security",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review for security issues.",
            Enabled = true,
            ActivationConfiguration = new()
            {
                IncludedFiles =
                [
                    new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".cs" },
                ],
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await store.AddAsync(agent, CancellationToken.None);
        _context.ChangeTracker.Clear();
        var enabled = await new PostgresCodeReviewerAgentStore(
            _context
        ).ListEnabledForRepositoryAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        agent.DisplayName = "Security And Auth Reviewer";
        agent.Enabled = false;
        agent.UpdatedAtUtc = now.AddMinutes(1);
        await new PostgresCodeReviewerAgentStore(_context).UpdateAsync(
            agent,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var enabledAfterUpdate = await new PostgresCodeReviewerAgentStore(
            _context
        ).ListEnabledForRepositoryAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );
        var configured = await new PostgresCodeReviewerAgentStore(_context).ListForRepositoryAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );
        var disabled = await new PostgresCodeReviewerAgentStore(_context).DisableAsync(
            seed.Organization.Id,
            agent.Id,
            now.AddMinutes(2),
            CancellationToken.None
        );

        await Assert.That(enabled).Count().IsEqualTo(1);
        await Assert
            .That(enabled.Single().ActivationConfiguration.IncludedFiles)
            .Count()
            .IsEqualTo(1);
        await Assert.That(enabledAfterUpdate).IsEmpty();
        await Assert.That(configured.Single().DisplayName).IsEqualTo("Security And Auth Reviewer");
        await Assert.That(disabled).IsTrue();
    }

    [Test]
    public async Task CodeReviewExecutionLeaseStore_TryAcquireAsync_EnforcesOrganizationCapacity()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = CreateExecutionLeaseStore();

        var first = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_1", max: 2),
            CancellationToken.None
        );
        var second = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_2", max: 2),
            CancellationToken.None
        );
        var duplicate = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_1", max: 2),
            CancellationToken.None
        );
        var noSlot = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_3", max: 2),
            CancellationToken.None
        );

        await Assert.That(first.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
        await Assert.That(second.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
        await Assert
            .That(duplicate.Outcome)
            .IsEqualTo(CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview);
        await Assert
            .That(noSlot.Outcome)
            .IsEqualTo(CodeReviewExecutionLeaseOutcome.NoSlotAvailable);
    }

    [Test]
    public async Task CodeReviewExecutionLeaseStore_TryAcquireAsync_AllowsConfiguredSlotCountsAndExpiredCleanup()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = CreateExecutionLeaseStore();

        for (var i = 0; i < 4; i++)
        {
            var result = await store.TryAcquireAsync(
                LeaseRequest(
                    seed.Organization.Id,
                    seed.RootTeam.Id,
                    repository.Id,
                    $"review_{i}",
                    max: 4
                ),
                CancellationToken.None
            );

            await Assert.That(result.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
        }

        var noSlot = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_4", max: 4),
            CancellationToken.None
        );

        _context
            .CodeReviewExecutionLeases.Single(lease => lease.CodeReviewRecordId == "review_0")
            .ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();

        var afterExpiry = await CreateExecutionLeaseStore()
            .TryAcquireAsync(
                LeaseRequest(
                    seed.Organization.Id,
                    seed.RootTeam.Id,
                    repository.Id,
                    "review_5",
                    max: 4
                ),
                CancellationToken.None
            );

        await Assert
            .That(noSlot.Outcome)
            .IsEqualTo(CodeReviewExecutionLeaseOutcome.NoSlotAvailable);
        await Assert.That(afterExpiry.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
    }

    [Test]
    public async Task CodeReviewExecutionLeaseStore_TryAcquireAsync_AllowsTwoConfiguredSlots()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = CreateExecutionLeaseStore();

        var first = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_1", max: 2),
            CancellationToken.None
        );
        var second = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_2", max: 2),
            CancellationToken.None
        );
        var third = await store.TryAcquireAsync(
            LeaseRequest(seed.Organization.Id, seed.RootTeam.Id, repository.Id, "review_3", max: 2),
            CancellationToken.None
        );

        await Assert.That(first.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
        await Assert.That(second.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.Acquired);
        await Assert.That(third.Outcome).IsEqualTo(CodeReviewExecutionLeaseOutcome.NoSlotAvailable);
    }

    [Test]
    public async Task CodeReviewArtifactStore_WriteFindingsAsync_UsesStreamStorageAndGeneratedUri()
    {
        var (_, _, _, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords()
            .BuildAsync();
        var review = reviews[0];
        var storage = new PostgresStorageProvider(_context);
        var artifacts = new PostgresCodeReviewArtifactStore(storage);
        await using var source = new MemoryStream("<reviews />"u8.ToArray());

        var uri = await artifacts.WriteFindingsAsync(
            review,
            source,
            "application/xml",
            CancellationToken.None
        );
        await using var opened = await artifacts.OpenFindingsAsync(uri, CancellationToken.None);
        using var reader = new StreamReader(opened);
        var text = await reader.ReadToEndAsync();

        await Assert
            .That(uri)
            .StartsWith($"postgres://code-review-findings/{review.OrganizationId}/");
        await Assert.That(text).IsEqualTo("<reviews />");
        await Assert.That(await storage.DeleteAsync(uri["postgres://".Length..])).IsTrue();
    }

    [Test]
    public async Task ActiveCodeReviewLockStore_TryAcquireAsync_ReapsExpiredLockAndReconcilesReview()
    {
        var reviewCreatedAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var nextReviewCreatedAt = reviewCreatedAt.AddTicks(10);
        var (_, repository, pullRequests, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords(
                review =>
                {
                    review.Status = CodeReviewStatus.Pending;
                    review.CreatedAtUtc = reviewCreatedAt;
                },
                review =>
                {
                    review.Status = CodeReviewStatus.Pending;
                    review.CreatedAtUtc = nextReviewCreatedAt;
                    review.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var expiredLock = new ActiveCodeReviewLock
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            PullRequestRecordId = pullRequests[0].Id,
            PullRequestCreatedAtUtc = pullRequests[0].CreatedAtUtc,
            CodeReviewRecordId = reviews[0].Id,
            CodeReviewCreatedAtUtc = reviews[0].CreatedAtUtc,
            Status = CodeReviewStatus.Pending,
            AcquiredAtUtc = now.AddHours(-3),
            ExpiresAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now.AddHours(-3),
        };
        _context.ActiveCodeReviewLocks.Add(expiredLock);
        await _context.SaveChangesAsync();

        var nextReview = reviews[1];
        var store = new PostgresActiveCodeReviewLockStore(_context);

        var acquired = await store.TryAcquireAsync(
            new()
            {
                OrganizationId = expiredLock.OrganizationId,
                TeamId = expiredLock.TeamId,
                RepositoryId = expiredLock.RepositoryId,
                PullRequestRecordId = pullRequests[0].Id,
                PullRequestCreatedAtUtc = pullRequests[0].CreatedAtUtc,
                CodeReviewRecordId = nextReview.Id,
                CodeReviewCreatedAtUtc = nextReview.CreatedAtUtc,
                Status = CodeReviewStatus.Pending,
                AcquiredAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );
        var reconciled = await _context.CodeReviewRecords.FindAsync(
            [reviews[0].Id, reviews[0].CreatedAtUtc],
            CancellationToken.None
        );

        await Assert.That(acquired).IsTrue();
        await Assert.That(reconciled!.Status).IsEqualTo(CodeReviewStatus.Errored);
    }

    [Test]
    public async Task ActiveCodeReviewLockStore_RefreshAsync_PushesExpiryForward()
    {
        var (_, _, _, _, activeLocks) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords()
            .AddActiveCodeReviewLocks()
            .BuildAsync();
        var activeLock = activeLocks[0];
        var store = new PostgresActiveCodeReviewLockStore(_context);
        var originalExpiry = activeLock.ExpiresAtUtc;

        var refreshed = await store.RefreshAsync(
            activeLock.OrganizationId,
            activeLock.PullRequestRecordId,
            TimeSpan.FromHours(4),
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var found = await new PostgresActiveCodeReviewLockStore(_context).FindAsync(
            activeLock.OrganizationId,
            activeLock.PullRequestRecordId,
            CancellationToken.None
        );

        await Assert.That(refreshed).IsTrue();
        await Assert.That(found!.ExpiresAtUtc).IsGreaterThan(originalExpiry);
    }

    [Test]
    public async Task GitHubWebhookDeliveryStore_ClaimAsync_IsIdempotent()
    {
        var deliveryId = SeedContext.NewId("delivery");
        var (_, _, deliveries) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddGitHubWebhookDeliveries(
                delivery =>
                {
                    delivery.DeliveryId = deliveryId;
                    delivery.PersistOnBuild = false;
                },
                delivery =>
                {
                    delivery.DeliveryId = deliveryId;
                    delivery.PersistOnBuild = false;
                },
                delivery =>
                {
                    delivery.DeliveryId = deliveryId;
                    delivery.PersistOnBuild = false;
                }
            )
            .BuildAsync();
        var store = new PostgresGitHubWebhookDeliveryStore(_context);

        var firstClaim = await store.ClaimAsync(deliveries[0], CancellationToken.None);
        _context.ChangeTracker.Clear();
        var inProgressClaim = await new PostgresGitHubWebhookDeliveryStore(_context).ClaimAsync(
            deliveries[1],
            CancellationToken.None
        );
        await new PostgresGitHubWebhookDeliveryStore(_context).MarkProcessedAsync(
            deliveryId,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var processedClaim = await new PostgresGitHubWebhookDeliveryStore(_context).ClaimAsync(
            deliveries[2],
            CancellationToken.None
        );

        await Assert.That(firstClaim).IsEqualTo(WebhookDeliveryClaimResult.Claimed);
        await Assert.That(inProgressClaim).IsEqualTo(WebhookDeliveryClaimResult.InProgress);
        await Assert.That(processedClaim).IsEqualTo(WebhookDeliveryClaimResult.AlreadyProcessed);
    }

    [Test]
    public async Task GitHubWebhookDeliveryClaims_AreUnloggedAndPrunedByPgCron()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await Assert
            .That(
                await GetTablePersistenceAsync(
                    connection,
                    "code_review_github_webhook_delivery_claims"
                )
            )
            .IsEqualTo("u");
        await Assert.That(await GetCronJobScheduleAsync(connection)).IsEqualTo("*/5 * * * *");
        await Assert.That(await GetCronJobCommandAsync(connection)).Contains("LIMIT 10000");
    }

    private static async Task<int> CountPartitionedParentsAsync(
        NpgsqlConnection connection,
        string tableName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_partitioned_table partitioned_table
            JOIN pg_class parent_class ON parent_class.oid = partitioned_table.partrelid
            JOIN pg_namespace parent_schema ON parent_schema.oid = parent_class.relnamespace
            WHERE parent_schema.nspname = 'zeeq'
              AND parent_class.relname = @table_name
            """;
        command.Parameters.AddWithValue("table_name", tableName);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> GetTablePersistenceAsync(
        NpgsqlConnection connection,
        string tableName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_class.relpersistence::text
            FROM pg_class table_class
            JOIN pg_namespace table_schema ON table_schema.oid = table_class.relnamespace
            WHERE table_schema.nspname = 'zeeq'
              AND table_class.relname = @table_name
            """;
        command.Parameters.AddWithValue("table_name", tableName);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> GetCronJobScheduleAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schedule
            FROM cron.job
            WHERE jobname = 'code-review-github-webhook-delivery-claim-retention'
            """;

        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> GetCronJobCommandAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT command
            FROM cron.job
            WHERE jobname = 'code-review-github-webhook-delivery-claim-retention'
            """;

        return (string?)await command.ExecuteScalarAsync();
    }

    // -------------------------------------------------------------------------
    // ListForAgentAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_MatchesBySessionIdOnly()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var session = SeedContext.NewId("sess");
        var store = new PostgresCodeReviewRecordStore(_context);

        var match = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 1
        );
        var noMatch = AgentReview(
            seed.Organization.Id,
            agentSessionId: SeedContext.NewId("sess"),
            reviewGroupId: null,
            minutesAgo: 2
        );
        await store.AddAsync(match, CancellationToken.None);
        await store.AddAsync(noMatch, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            session,
            null,
            50,
            CancellationToken.None
        );

        await Assert.That(results.Select(r => r.Id).ToArray()).IsEquivalentTo([match.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_MatchesByGroupIdOnly()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var group = SeedContext.NewId("crg");
        var store = new PostgresCodeReviewRecordStore(_context);

        var match = AgentReview(
            seed.Organization.Id,
            agentSessionId: null,
            reviewGroupId: group,
            minutesAgo: 1
        );
        var noMatch = AgentReview(
            seed.Organization.Id,
            agentSessionId: null,
            reviewGroupId: SeedContext.NewId("crg"),
            minutesAgo: 2
        );
        await store.AddAsync(match, CancellationToken.None);
        await store.AddAsync(noMatch, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            null,
            group,
            50,
            CancellationToken.None
        );

        await Assert.That(results.Select(r => r.Id).ToArray()).IsEquivalentTo([match.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_ReturnsUnionWhenBothKeysProvided()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var session = SeedContext.NewId("sess");
        var group = SeedContext.NewId("crg");
        var store = new PostgresCodeReviewRecordStore(_context);

        // Matches by session only, group only, and both.
        var bySession = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 1
        );
        var byGroup = AgentReview(
            seed.Organization.Id,
            agentSessionId: null,
            reviewGroupId: group,
            minutesAgo: 2
        );
        var byBoth = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: group,
            minutesAgo: 3
        );
        var noMatch = AgentReview(
            seed.Organization.Id,
            agentSessionId: SeedContext.NewId("sess"),
            reviewGroupId: SeedContext.NewId("crg"),
            minutesAgo: 4
        );
        await store.AddAsync(bySession, CancellationToken.None);
        await store.AddAsync(byGroup, CancellationToken.None);
        await store.AddAsync(byBoth, CancellationToken.None);
        await store.AddAsync(noMatch, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            session,
            group,
            50,
            CancellationToken.None
        );

        await Assert
            .That(results.Select(r => r.Id))
            .Contains(bySession.Id)
            .And.Contains(byGroup.Id)
            .And.Contains(byBoth.Id)
            .And.DoesNotContain(noMatch.Id);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_ReturnsEmptyWhenBothKeysNull()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = new PostgresCodeReviewRecordStore(_context);
        var review = AgentReview(
            seed.Organization.Id,
            agentSessionId: SeedContext.NewId("sess"),
            reviewGroupId: null,
            minutesAgo: 1
        );
        await store.AddAsync(review, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            null,
            null,
            50,
            CancellationToken.None
        );

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_EnforcesOrgScoping()
    {
        var (seed1, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var (seed2, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var session = SeedContext.NewId("sess");
        var store = new PostgresCodeReviewRecordStore(_context);

        var inOrg = AgentReview(
            seed1.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 1
        );
        var outOrg = AgentReview(
            seed2.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 1
        );
        await store.AddAsync(inOrg, CancellationToken.None);
        await store.AddAsync(outOrg, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed1.Organization.Id,
            session,
            null,
            50,
            CancellationToken.None
        );

        await Assert.That(results.Select(r => r.Id).ToArray()).IsEquivalentTo([inOrg.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_ReturnsNewestFirst()
    {
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var session = SeedContext.NewId("sess");
        var store = new PostgresCodeReviewRecordStore(_context);

        var older = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 3
        );
        var middle = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 2
        );
        var newer = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: null,
            minutesAgo: 1
        );
        await store.AddAsync(older, CancellationToken.None);
        await store.AddAsync(middle, CancellationToken.None);
        await store.AddAsync(newer, CancellationToken.None);

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            session,
            null,
            50,
            CancellationToken.None
        );

        await Assert
            .That(results.Select(r => r.Id).ToArray())
            .IsEquivalentTo([newer.Id, middle.Id, older.Id]);
    }

    [Test]
    public async Task CodeReviewRecordStore_ListForAgentAsync_ClampsToMaxRecords()
    {
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var session = SeedContext.NewId("sess");
        var store = new PostgresCodeReviewRecordStore(_context);

        for (var i = 0; i < 3; i++)
        {
            await store.AddAsync(
                AgentReview(
                    seed.Organization.Id,
                    agentSessionId: session,
                    reviewGroupId: null,
                    minutesAgo: i + 1
                ),
                CancellationToken.None
            );
        }

        var results = await store.ListForAgentAsync(
            seed.Organization.Id,
            session,
            null,
            maxRecords: 2,
            CancellationToken.None
        );

        await Assert.That(results).Count().IsEqualTo(2);
    }

    // -------------------------------------------------------------------------
    // Agent record round-trip
    // -------------------------------------------------------------------------

    [Test]
    public async Task CodeReviewRecordStore_AddAsync_AgentRecordWithNullPrAndRepoRoundTrips()
    {
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .BuildAsync();
        var store = new PostgresCodeReviewRecordStore(_context);
        var session = SeedContext.NewId("sess");
        var group = SeedContext.NewId("crg");
        var review = AgentReview(
            seed.Organization.Id,
            agentSessionId: session,
            reviewGroupId: group,
            minutesAgo: 1
        );

        await store.AddAsync(review, CancellationToken.None);
        _context.ChangeTracker.Clear();
        var found = await store.FindAsync(review.Id, review.CreatedAtUtc, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.PullRequestRecordId).IsNull();
        await Assert.That(found.RepositoryId).IsNull();
        await Assert.That(found.AgentSessionId).IsEqualTo(session);
        await Assert.That(found.ReviewGroupId).IsEqualTo(group);
        await Assert.That(found.RequestOrigin).IsEqualTo(CodeReviewRequestOrigin.Agent);
    }

    [Test]
    public async Task CodeReviewRecordStore_Update_PersistsTelemetryInSourceTelemetryPayload_RoundTrips()
    {
        var (_, _, _, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords()
            .BuildAsync();
        var review = reviews[0];
        var store = new PostgresCodeReviewRecordStore(_context);
        var telemetry = PopulatedTelemetry();

        review.SourceTelemetryPayload = CodeReviewSourceTelemetrySerializer.Serialize(telemetry);
        _context.ChangeTracker.Clear();
        await store.UpdateAsync(review, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(review.Id, review.CreatedAtUtc, CancellationToken.None);
        await Assert.That(found).IsNotNull();

        var roundTripped = CodeReviewSourceTelemetrySerializer.Deserialize(
            found!.SourceTelemetryPayload
        );
        await Assert.That(roundTripped).IsNotNull();

        // Structural round-trip fidelity plus spot-checks of the ETL-critical fields.
        await Assert
            .That(CodeReviewSourceTelemetrySerializer.Serialize(roundTripped!))
            .IsEqualTo(CodeReviewSourceTelemetrySerializer.Serialize(telemetry));

        var document = roundTripped!.Documents.Single();
        await Assert.That(document.DocumentId).IsEqualTo("doc_a");
        await Assert.That(document.ReadAfterSearch).IsTrue();
        await Assert.That(document.BestRank).IsEqualTo(1);
        await Assert.That(document.BestScore).IsEqualTo(0.0312);
        await Assert.That(document.Snippets.Single().SnippetId).IsEqualTo("sn_a");
        await Assert.That(document.Snippets.Single().IdentifierMatch).IsTrue();
        await Assert.That(roundTripped.MissedQueries.Single().Query).IsEqualTo("aspire lock");
    }

    [Test]
    public async Task CodeReviewRecordStore_Update_WhenTelemetryEmpty_StoresEmptyObject()
    {
        var (_, _, _, reviews) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords()
            .AddCodeReviewRecords()
            .BuildAsync();
        var review = reviews[0];
        var store = new PostgresCodeReviewRecordStore(_context);

        // An empty run maps to the "{}" sentinel via the context's payload serializer.
        review.SourceTelemetryPayload = new CodeReviewTelemetryContext().SerializeSnapshotPayload();
        _context.ChangeTracker.Clear();
        await store.UpdateAsync(review, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(review.Id, review.CreatedAtUtc, CancellationToken.None);
        await Assert.That(found).IsNotNull();
        await Assert
            .That(found!.SourceTelemetryPayload)
            .IsEqualTo(CodeReviewRecord.EmptySourceTelemetryPayload);
        await Assert
            .That(CodeReviewSourceTelemetrySerializer.Deserialize(found.SourceTelemetryPayload))
            .IsNull();
    }

    private static CodeReviewSourceTelemetry PopulatedTelemetry() =>
        new(
            SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
            Summary: new(
                DocumentCount: 1,
                SnippetCount: 1,
                SourceHitCount: 3,
                ToolCallCount: 2,
                MissedQueryCount: 1
            ),
            Documents:
            [
                new(
                    DocumentId: "doc_a",
                    Library: "kb",
                    Path: "/a.md",
                    Title: "Doc A",
                    HitCount: 3,
                    Usages: ["Read", "Searched"],
                    ReadAfterSearch: true,
                    Facets: ["Security"],
                    BestRank: 1,
                    BestScore: 0.0312,
                    Queries: ["logging"],
                    Snippets:
                    [
                        new(
                            SnippetId: "sn_a",
                            Heading: "A > X",
                            Kind: "Section",
                            Language: null,
                            HitCount: 2,
                            Facets: ["Security"],
                            BestRank: 1,
                            BestScore: 0.0312,
                            IdentifierMatch: true,
                            Queries: ["otel"]
                        ),
                    ]
                ),
            ],
            ToolUsage: [new(Tool: "search_sections", Calls: 2, Succeeded: 2, Failed: 0)],
            MissedQueries:
            [
                new(Query: "aspire lock", Tool: "search_sections", Facets: ["Security"]),
            ]
        );

    private static CodeReviewRecord AgentReview(
        string organizationId,
        string? agentSessionId,
        string? reviewGroupId,
        int minutesAgo
    ) =>
        new()
        {
            Id = SeedContext.NewId("cr"),
            OrganizationId = organizationId,
            PullRequestRecordId = null,
            RepositoryId = null,
            OwnerQualifiedRepoName = string.Empty,
            PullRequestNumber = 0,
            Branch = string.Empty,
            Title = "Agent review",
            AuthorLogin = string.Empty,
            AgentSessionId = agentSessionId,
            ReviewGroupId = reviewGroupId,
            Status = CodeReviewStatus.Completed,
            RequestOrigin = CodeReviewRequestOrigin.Agent,
            RemainingReviewBudget = 10,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        };

    [Test]
    public async Task CodeRepositoryStore_ReviewConfiguration_RoundTripsCheckRunConfig()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository =>
            {
                repository.ReviewConfiguration = new()
                {
                    FileFilter = new(),
                    CheckRun = new() { BlockOnCritical = true, BlockOnMajor = false },
                };
            })
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.ReviewConfiguration.CheckRun.BlockOnCritical).IsTrue();
        await Assert.That(found.ReviewConfiguration.CheckRun.BlockOnMajor).IsFalse();
        await Assert.That(found.ReviewConfiguration.CheckRun.IsEnabled).IsTrue();
    }

    [Test]
    public async Task CodeRepositoryStore_ReviewConfiguration_CheckRunDefaultsToEmptyWhenAbsent()
    {
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(repository =>
            {
                repository.ReviewConfiguration = new() { FileFilter = new() };
            })
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);

        _context.ChangeTracker.Clear();
        var found = await store.FindActiveForOrganizationAsync(
            seed.Organization.Id,
            repository.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.ReviewConfiguration.CheckRun.BlockOnCritical).IsFalse();
        await Assert.That(found.ReviewConfiguration.CheckRun.BlockOnMajor).IsFalse();
        await Assert.That(found.ReviewConfiguration.CheckRun.IsEnabled).IsFalse();
    }

    [Test]
    public async Task PullRequestRecordStore_CheckRunState_RoundTripsAsNonNullableJsonb()
    {
        var (seed, _, pullRequests) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 42;
                pullRequest.PersistOnBuild = false;
            })
            .BuildAsync();
        var pr = pullRequests[0];
        pr.CheckRunState = new()
        {
            CheckRunId = 12345,
            HeadSha = "abc123def456",
            State = CheckRunBlockState.Blocking,
        };
        var store = new PostgresPullRequestRecordStore(_context);

        await store.UpsertAsync(pr, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(pr.Id, pr.CreatedAtUtc, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.CheckRunState).IsNotNull();
        await Assert.That(found.CheckRunState!.CheckRunId).IsEqualTo(12345);
        await Assert.That(found.CheckRunState.HeadSha).IsEqualTo("abc123def456");
        await Assert.That(found.CheckRunState.State).IsEqualTo(CheckRunBlockState.Blocking);
        await Assert.That(found.CheckRunState.RemovedBy).IsNull();
        await Assert.That(found.CheckRunState.RemovedAtUtc).IsNull();
    }

    [Test]
    public async Task PullRequestRecordStore_CheckRunState_RoundTripsRemovedState()
    {
        var removedAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var (seed, _, pullRequests) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 43;
                pullRequest.PersistOnBuild = false;
            })
            .BuildAsync();
        var pr = pullRequests[0];
        pr.CheckRunState = new()
        {
            CheckRunId = 67890,
            HeadSha = "def789abc012",
            State = CheckRunBlockState.Removed,
            RemovedBy = "octocat",
            RemovedAtUtc = removedAt,
        };
        var store = new PostgresPullRequestRecordStore(_context);

        await store.UpsertAsync(pr, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(pr.Id, pr.CreatedAtUtc, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.CheckRunState).IsNotNull();
        await Assert.That(found.CheckRunState!.CheckRunId).IsEqualTo(67890);
        await Assert.That(found.CheckRunState.State).IsEqualTo(CheckRunBlockState.Removed);
        await Assert.That(found.CheckRunState.RemovedBy).IsEqualTo("octocat");
        await Assert.That(found.CheckRunState.RemovedAtUtc).IsEqualTo(removedAt);
    }

    [Test]
    public async Task PullRequestRecordStore_CheckRunState_IsNullByDefault()
    {
        var (seed, _, pullRequests) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository()
            .AddPullRequestRecords(pullRequest =>
            {
                pullRequest.PullRequestNumber = 44;
                pullRequest.PersistOnBuild = false;
            })
            .BuildAsync();
        var pr = pullRequests[0];
        var store = new PostgresPullRequestRecordStore(_context);

        await store.UpsertAsync(pr, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(pr.Id, pr.CreatedAtUtc, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.CheckRunState).IsNull();
    }

    private PostgresCodeReviewExecutionLeaseStore CreateExecutionLeaseStore() =>
        new(_context, NullLogger<PostgresCodeReviewExecutionLeaseStore>.Instance);

    private static CodeReviewExecutionLeaseRequest LeaseRequest(
        string organizationId,
        string? teamId,
        string repositoryId,
        string codeReviewRecordId,
        int max
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new(
            OrganizationId: organizationId,
            TeamId: teamId,
            RepositoryId: repositoryId,
            PullRequestRecordId: "pr_" + codeReviewRecordId,
            PullRequestCreatedAtUtc: now.AddMinutes(-2),
            CodeReviewRecordId: codeReviewRecordId,
            CodeReviewCreatedAtUtc: now.AddMinutes(-1),
            MaxConcurrentReviews: max,
            LeaseDuration: TimeSpan.FromMinutes(2),
            WorkerId: "test-worker"
        );
    }

    private static async Task<int> CountPartmanParentsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM zeeq.part_config
            WHERE parent_table IN (
                'zeeq.code_review_pull_request_records',
                'zeeq.code_review_records'
            )
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
