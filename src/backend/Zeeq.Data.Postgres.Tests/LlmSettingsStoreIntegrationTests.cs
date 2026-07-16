using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.LlmSettings;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for Postgres-backed LLM settings stores.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/LlmSettingsStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LlmSettingsStoreIntegrationTests : PgTransactionalTestBase
{
    public LlmSettingsStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task LlmSettingsStore_ReadsAndUpdatesTypedConfigurationByOrganization()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var updatedAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var configuration = new OrganizationLlmConfiguration
        {
            Fast = new OrganizationLlmTierConfiguration
            {
                Provider = "OpenAI",
                Model = "gpt-5.4-mini",
                KeyId = "key_fast",
            },
            High = new OrganizationLlmTierConfiguration
            {
                Provider = "Anthropic",
                Model = "claude-sonnet-4-6",
                KeyId = "key_high",
            },
            Max = new OrganizationLlmTierConfiguration
            {
                Provider = "Anthropic",
                Model = "claude-opus-4-8",
                KeyId = "key_max",
            },
        };

        ILlmSettingsStore store = new PostgresLlmSettingsStore(_context);

        var updated = await store.UpdateConfigurationAsync(
            seed.Organization.Id,
            configuration,
            updatedAt,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();

        var found = await new PostgresLlmSettingsStore(_context).FindConfigurationAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        var persistedUpdatedAt = await _context
            .Organizations.Where(organization => organization.Id == seed.Organization.Id)
            .Select(organization => organization.UpdatedAtUtc)
            .SingleAsync();

        var missing = await store.FindConfigurationAsync("org_missing", CancellationToken.None);

        var missingUpdated = await store.UpdateConfigurationAsync(
            "org_missing",
            configuration,
            updatedAt,
            CancellationToken.None
        );

        await Assert.That(updated).IsTrue();
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Fast.Provider).IsEqualTo("OpenAI");
        await Assert.That(found.Fast.Model).IsEqualTo("gpt-5.4-mini");
        await Assert.That(found.High.Provider).IsEqualTo("Anthropic");
        await Assert.That(found.High.Model).IsEqualTo("claude-sonnet-4-6");
        await Assert.That(found.Max.Model).IsEqualTo("claude-opus-4-8");
        await Assert.That(persistedUpdatedAt).IsEqualTo(updatedAt);
        await Assert.That(missing).IsNull();
        await Assert.That(missingUpdated).IsFalse();
    }

    [Test]
    public async Task LlmSettingsStore_TreatsLegacyDeepSeekDefaultAsNoConfiguration()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        // Simulate an org still carrying the pre-Fireworks migration-seeded default.
        var organization = await _context
            .Organizations.Where(org => org.Id == seed.Organization.Id)
            .FirstAsync();
        organization.LlmConfiguration = OrganizationLlmConfiguration.LegacyDeepSeekDefault;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await new PostgresLlmSettingsStore(_context).FindConfigurationAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task EncryptedValueStore_EnforcesOrganizationScopeAndActiveRows()
    {
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddOrganizations(1)
            .BuildAsync();

        var organizationId = seed.Organization.Id;
        var otherOrganizationId = organizationGraphs[0].Organization.Id;
        var sharedKeyId = SeedContext.NewId("secret");
        var disabledKeyId = SeedContext.NewId("secret");
        var now = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();

        IEncryptedValueStore store = new PostgresEncryptedValueStore(_context);

        await store.AddAsync(
            NewEncryptedValue(
                organizationId,
                sharedKeyId,
                name: "Fast key",
                ciphertext: [1, 2, 3],
                now
            ),
            CancellationToken.None
        );
        await store.AddAsync(
            NewEncryptedValue(
                organizationId,
                disabledKeyId,
                name: "Disabled key",
                ciphertext: [4, 5, 6],
                now,
                disabledAtUtc: now.AddMinutes(1)
            ),
            CancellationToken.None
        );
        await store.AddAsync(
            NewEncryptedValue(
                otherOrganizationId,
                sharedKeyId,
                name: "Other org key",
                ciphertext: [7, 8, 9],
                now
            ),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();

        store = new PostgresEncryptedValueStore(_context);

        var organizationKeys = await store.ListActiveAsync(
            organizationId,
            EncryptedValueKind.LlmApiKey,
            CancellationToken.None
        );

        var found = await store.FindActiveAsync(
            organizationId,
            sharedKeyId,
            CancellationToken.None
        );

        var disabled = await store.FindActiveAsync(
            organizationId,
            disabledKeyId,
            CancellationToken.None
        );

        var otherOrganizationValue = await store.FindActiveAsync(
            otherOrganizationId,
            sharedKeyId,
            CancellationToken.None
        );

        await Assert
            .That(organizationKeys.Select(value => value.Id).ToArray())
            .IsEquivalentTo([sharedKeyId]);
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Name).IsEqualTo("Fast key");
        await Assert.That(Convert.ToHexString(found.Ciphertext)).IsEqualTo("010203");
        await Assert.That(disabled).IsNull();
        await Assert.That(otherOrganizationValue).IsNotNull();
        await Assert.That(otherOrganizationValue!.Name).IsEqualTo("Other org key");

        var rotatedAt = now.AddMinutes(2);
        var updated = await store.UpdateAsync(
            NewEncryptedValue(
                organizationId,
                sharedKeyId,
                name: "Rotated key",
                ciphertext: [10, 11, 12],
                now: rotatedAt,
                encryptionProvider: "cloud-kms"
            ),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();

        var rotated = await new PostgresEncryptedValueStore(_context).FindActiveAsync(
            organizationId,
            sharedKeyId,
            CancellationToken.None
        );

        await Assert.That(updated).IsTrue();
        await Assert.That(rotated).IsNotNull();
        await Assert.That(rotated!.Name).IsEqualTo("Rotated key");
        await Assert.That(rotated.EncryptionProvider).IsEqualTo("cloud-kms");
        await Assert.That(Convert.ToHexString(rotated.Ciphertext)).IsEqualTo("0A0B0C");
        await Assert.That(rotated.UpdatedAtUtc).IsEqualTo(rotatedAt);

        var disabledAt = now.AddMinutes(3);
        var disableResult = await new PostgresEncryptedValueStore(_context).DisableAsync(
            organizationId,
            sharedKeyId,
            disabledAt,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var afterDisable = await new PostgresEncryptedValueStore(_context).FindActiveAsync(
            organizationId,
            sharedKeyId,
            CancellationToken.None
        );
        var afterDisableList = await new PostgresEncryptedValueStore(_context).ListActiveAsync(
            organizationId,
            EncryptedValueKind.LlmApiKey,
            CancellationToken.None
        );
        var secondDisable = await new PostgresEncryptedValueStore(_context).DisableAsync(
            organizationId,
            sharedKeyId,
            disabledAt,
            CancellationToken.None
        );
        var updateDisabled = await new PostgresEncryptedValueStore(_context).UpdateAsync(
            NewEncryptedValue(
                organizationId,
                sharedKeyId,
                name: "Should not update",
                ciphertext: [13],
                now.AddMinutes(4)
            ),
            CancellationToken.None
        );
        var otherOrgStillActive = await new PostgresEncryptedValueStore(_context).FindActiveAsync(
            otherOrganizationId,
            sharedKeyId,
            CancellationToken.None
        );

        await Assert.That(disableResult).IsTrue();
        await Assert.That(afterDisable).IsNull();
        await Assert.That(afterDisableList).IsEmpty();
        await Assert.That(secondDisable).IsFalse();
        await Assert.That(updateDisabled).IsFalse();
        await Assert.That(otherOrgStillActive).IsNotNull();
        await Assert.That(otherOrgStillActive!.Name).IsEqualTo("Other org key");
    }

    private static EncryptedValue NewEncryptedValue(
        string organizationId,
        string id,
        string name,
        byte[] ciphertext,
        DateTimeOffset now,
        string encryptionProvider = "data-protection",
        DateTimeOffset? disabledAtUtc = null
    ) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Kind = EncryptedValueKind.LlmApiKey,
            EncryptionProvider = encryptionProvider,
            Name = name,
            Ciphertext = ciphertext,
            CreatedAtUtc = now,
            DisabledAtUtc = disabledAtUtc,
            UpdatedAtUtc = now,
        };
}
