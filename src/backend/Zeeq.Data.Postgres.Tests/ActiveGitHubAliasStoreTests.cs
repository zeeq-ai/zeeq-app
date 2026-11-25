using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

[NotInParallel]
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ActiveGitHubAliasStoreTests(PgDatabaseFixture postgres)
{
    [Test]
    [Arguments(MembershipStatus.Active, false, false, true)]
    [Arguments(MembershipStatus.Pending, false, false, false)]
    [Arguments(MembershipStatus.Active, true, false, false)]
    [Arguments(MembershipStatus.Active, false, true, false)]
    public async Task Lookup_RequiresActiveMembership(
        MembershipStatus status,
        bool disabledMembership,
        bool disabledAlias,
        bool expected
    )
    {
        var id = "alias_test_" + Guid.NewGuid().ToString("N");
        await using var db = postgres.CreateContext();
        db.Users.Add(new User { Id = id, DisplayName = id });
        db.Organizations.Add(
            new Organization
            {
                Id = id,
                DisplayName = id,
                CreatedByUserId = id,
            }
        );
        db.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = id,
                OrganizationId = id,
                UserId = id,
                Role = "member",
                Status = status,
                CreatedByUserId = id,
                DisabledAtUtc = disabledMembership ? DateTimeOffset.UtcNow : null,
            }
        );
        db.UserAliases.Add(
            new UserAlias
            {
                Id = id,
                OrganizationId = id,
                UserId = id,
                Kind = UserAliasKind.GitHub,
                DisplayValue = "Alice",
                NormalizedValue = "alice",
                DisabledAtUtc = disabledAlias ? DateTimeOffset.UtcNow : null,
            }
        );
        try
        {
            await db.SaveChangesAsync();
            var store = new PostgresZeeqIdentityStore(db, new AppSettings());
            var found = await store.FindActiveGitHubAliasAsync(id, "alice", CancellationToken.None);
            await Assert.That(found is not null).IsEqualTo(expected);
            await Assert
                .That(
                    await store.FindActiveGitHubAliasAsync("other", "alice", CancellationToken.None)
                )
                .IsNull();
        }
        finally
        {
            await db.UserAliases.Where(x => x.OrganizationId == id).ExecuteDeleteAsync();
            await db
                .OrganizationMemberships.Where(x => x.OrganizationId == id)
                .ExecuteDeleteAsync();
            await db.Organizations.Where(x => x.Id == id).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        }
    }
}
