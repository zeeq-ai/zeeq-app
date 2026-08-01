using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresOrganizationActivationKeyStoreTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task ConsumeKeyAndActivateOrganizationAsync_ValidKey_StoresHashAndActivatesOnce()
    {
        // Guards that activation keys are stored as hashes and can be consumed
        // exactly once to activate one eligible inactive organization.
        var now = DateTimeOffset.UtcNow;
        var rawKey = OrganizationActivationKeyMaterial.GenerateKey();
        var keyHash = OrganizationActivationKeyMaterial.ComputeHash(rawKey);
        var store = new PostgresOrganizationActivationKeyStore(_context);
        var (seed, admins, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Inactive Org";
                    organization.ActivatedAtUtc = null;
                }
            )
            .AddUsers(admin => admin.DisplayName = "Admin")
            .AddOrganizations(organization =>
            {
                organization.DisplayName = "Second Inactive Org";
                organization.IsActivated = false;
            })
            .BuildAsync();
        var admin = admins[0];
        var secondOrganization = organizationGraphs[0].Organization;

        await store.CreateKeyAsync(
            new OrganizationActivationKey
            {
                Id = "oak_" + Guid.NewGuid().ToString("N"),
                KeyHash = keyHash,
                CreatedByUserId = admin.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(30),
            },
            CancellationToken.None
        );

        var persistedKey = await _context.OrganizationActivationKeys.SingleAsync();
        await Assert.That(persistedKey.KeyHash).IsEqualTo(keyHash);
        await Assert.That(persistedKey.KeyHash).IsNotEqualTo(rawKey);

        var listedByCreator = await store.ListKeysAsync(
            1,
            25,
            "Admin",
            null,
            CancellationToken.None
        );
        await Assert.That(listedByCreator.TotalCount).IsEqualTo(1);
        await Assert.That(listedByCreator.Items[0].CreatedByDisplayName).IsEqualTo("Admin");
        await Assert.That(listedByCreator.Items[0].CreatedByUserId).IsEqualTo(admin.Id);

        var result = await store.ConsumeKeyAndActivateOrganizationAsync(
            keyHash,
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );
        var secondResult = await store.ConsumeKeyAndActivateOrganizationAsync(
            keyHash,
            secondOrganization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var activatedOrganization = await _context.Organizations.SingleAsync(org =>
            org.Id == seed.Organization.Id
        );
        var activatedKey = await _context.OrganizationActivationKeys.SingleAsync();

        await Assert.That(result).IsEqualTo(OrganizationActivationExchangeResult.Activated);
        await Assert.That(secondResult).IsEqualTo(OrganizationActivationExchangeResult.InvalidKey);
        await Assert.That(activatedOrganization.ActivatedAtUtc).IsNotNull();
        await Assert.That(activatedKey.ActivatedAtUtc).IsNotNull();
        await Assert.That(activatedKey.ActivatedOrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(activatedKey.ActivatedByUserId).IsEqualTo(seed.Owner.Id);
    }

    [Test]
    public async Task ConsumeKeyAndActivateOrganizationAsync_MissingOrganization_DoesNotConsumeKey()
    {
        // Guards that organization eligibility is established before the key
        // row receives activation foreign keys, avoiding FK failures and key loss.
        var now = DateTimeOffset.UtcNow;
        var rawKey = OrganizationActivationKeyMaterial.GenerateKey();
        var keyHash = OrganizationActivationKeyMaterial.ComputeHash(rawKey);
        var store = new PostgresOrganizationActivationKeyStore(_context);
        var seed = await EntityGraph
            .AddGeneratedSeed(_context, organization => organization.ActivatedAtUtc = null)
            .BuildAsync();

        await store.CreateKeyAsync(
            new OrganizationActivationKey
            {
                Id = "oak_" + Guid.NewGuid().ToString("N"),
                KeyHash = keyHash,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(30),
            },
            CancellationToken.None
        );

        var result = await store.ConsumeKeyAndActivateOrganizationAsync(
            keyHash,
            "org_missing",
            "usr_missing",
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var activationKey = await _context.OrganizationActivationKeys.SingleAsync();

        await Assert
            .That(result)
            .IsEqualTo(OrganizationActivationExchangeResult.InvalidOrganization);
        await Assert.That(activationKey.ActivatedAtUtc).IsNull();
        await Assert.That(activationKey.ActivatedOrganizationId).IsNull();
        await Assert.That(activationKey.ActivatedByUserId).IsNull();
    }
}
