using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Integration tests for the Postgres identity store user/bootstrap flow.
/// </summary>
/// <param name="postgres">Shared Postgres fixture for transactional tests.</param>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresZeeqIdentityStoreTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    /// <summary>
    /// Verifies that new-user bootstrap creates an organization slug with the ID suffix.
    /// </summary>
    [Test]
    public async Task EnsureUserAsync_NewUser_CreatesOrganizationSlugWithIdSuffix()
    {
        var context = await new PostgresZeeqIdentityStore(_context).EnsureUserAsync(
            "mock",
            Guid.NewGuid().ToString("N"),
            "My Org",
            null,
            null,
            CancellationToken.None
        );

        var organization = await _context.Organizations.SingleAsync(org =>
            org.Id == context.OrganizationId
        );

        // Guards that first-login organizations get a stable URL-safe slug while
        // avoiding collisions by suffixing the generated organization id.
        await Assert.That(organization.Slug).IsEqualTo($"my-org-{context.OrganizationId[^8..]}");
    }
}
