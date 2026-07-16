using System.Security.Claims;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Shared Postgres test setup for membership integration tests.
/// </summary>
/// <remarks>
/// The base <see cref="PgTransactionalTestBase"/> provides rollback isolation.
/// Tests that need to exercise production-owned transactions can use
/// <see cref="Postgres"/> to create a fresh context outside that ambient test
/// transaction.
/// </remarks>
public abstract class MembershipIntegrationTestBase : PgTransactionalTestBase
{
    protected readonly PgDatabaseFixture Postgres;

    protected MembershipIntegrationTestBase(PgDatabaseFixture postgres)
        : base(postgres)
    {
        Postgres = postgres;
    }

    protected IZeeqMembershipStore CreateStore() => new PostgresZeeqMembershipStore(_context);

    protected static ClaimsPrincipal TestUser(string userId, string email = "user@test.com") =>
        MembershipTestClaims.TestUser(userId, email);

    /// <summary>
    /// Ensures a default test user exists so that FK references
    /// (CreatedByUserId, UserId) are satisfied.
    /// </summary>
    [Before(Test)]
    public async Task SetupUser()
    {
        if (!await _context.Users.AnyAsync(u => u.Id == "test_user"))
        {
            _context.Users.Add(
                new User
                {
                    Id = "test_user",
                    DisplayName = "Test User",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                }
            );
            await _context.SaveChangesAsync();
        }
    }

    protected static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

    protected static async Task SeedUser(PostgresDbContext context, string userId)
    {
        if (await context.Users.AnyAsync(u => u.Id == userId))
        {
            return;
        }

        context.Users.Add(
            new User
            {
                Id = userId,
                DisplayName = "User " + userId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        await context.SaveChangesAsync();
    }
}
