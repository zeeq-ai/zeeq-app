using System.Data.Common;
using System.Text.Json;
using Zeeq.Core.Models;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for organization LLM configuration and encrypted value mapping.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/LlmSettingsMappingIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LlmSettingsMappingIntegrationTests : PgTransactionalTestBase
{
    public LlmSettingsMappingIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task OrganizationLlmConfiguration_RoundTripsThroughJsonbColumn()
    {
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.LlmConfiguration = new OrganizationLlmConfiguration
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
                }
            )
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var reloaded = await _context.Organizations.SingleAsync(organization =>
            organization.Id == seed.Organization.Id
        );
        var columnType = await ExecuteScalarAsync<string>(
            "SELECT pg_typeof(llm_configuration)::text FROM zeeq.core_organizations WHERE id = @id",
            ("id", seed.Organization.Id)
        );
        var storedJson = await ExecuteScalarAsync<string>(
            "SELECT llm_configuration::text FROM zeeq.core_organizations WHERE id = @id",
            ("id", seed.Organization.Id)
        );

        await Assert.That(reloaded.LlmConfiguration.Fast.Provider).IsEqualTo("OpenAI");
        await Assert.That(reloaded.LlmConfiguration.Fast.Model).IsEqualTo("gpt-5.4-mini");
        await Assert.That(reloaded.LlmConfiguration.Fast.KeyId).IsEqualTo("key_fast");
        await Assert.That(reloaded.LlmConfiguration.High.Provider).IsEqualTo("Anthropic");
        await Assert.That(reloaded.LlmConfiguration.High.Model).IsEqualTo("claude-sonnet-4-6");
        await Assert.That(reloaded.LlmConfiguration.Max.Model).IsEqualTo("claude-opus-4-8");
        await Assert.That(columnType).IsEqualTo("jsonb");

        using var document = JsonDocument.Parse(storedJson);
        await Assert
            .That(document.RootElement.GetProperty("Fast").GetProperty("Provider").GetString())
            .IsEqualTo("OpenAI");
        await Assert
            .That(document.RootElement.GetProperty("Max").GetProperty("KeyId").GetString())
            .IsEqualTo("key_max");
    }

    [Test]
    public async Task OrganizationLlmConfiguration_DefaultsToFireworksTiers()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var storedJson = await ExecuteScalarAsync<string>(
            "SELECT llm_configuration::text FROM zeeq.core_organizations WHERE id = @id",
            ("id", seed.Organization.Id)
        );

        using var document = JsonDocument.Parse(storedJson);
        await Assert
            .That(document.RootElement.GetProperty("Fast").GetProperty("Provider").GetString())
            .IsEqualTo("Fireworks");
        await Assert
            .That(document.RootElement.GetProperty("Fast").GetProperty("Model").GetString())
            .IsEqualTo("accounts/fireworks/models/deepseek-v4-flash");
        await Assert
            .That(document.RootElement.GetProperty("High").GetProperty("Model").GetString())
            .IsEqualTo("accounts/fireworks/models/deepseek-v4-pro");
        await Assert
            .That(document.RootElement.GetProperty("Max").GetProperty("Model").GetString())
            .IsEqualTo("accounts/fireworks/models/glm-5p2");
    }

    [Test]
    public async Task EncryptedValue_PersistsKindAsStringAndCiphertextAsBytes()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var encryptedValue = new EncryptedValue
        {
            Id = SeedContext.NewId("secret"),
            OrganizationId = seed.Organization.Id,
            Kind = EncryptedValueKind.LlmApiKey,
            EncryptionProvider = "data-protection",
            Name = "OpenAI key",
            Ciphertext = [1, 2, 3, 4],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        _context.EncryptedValues.Add(encryptedValue);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var reloaded = await _context.EncryptedValues.SingleAsync(value =>
            value.Id == encryptedValue.Id
        );
        var storedKind = await ExecuteScalarAsync<string>(
            "SELECT kind FROM zeeq.core_encrypted_values WHERE id = @id",
            ("id", encryptedValue.Id)
        );

        await Assert.That(reloaded.Kind).IsEqualTo(EncryptedValueKind.LlmApiKey);
        await Assert.That(reloaded.EncryptionProvider).IsEqualTo("data-protection");
        await Assert.That(Convert.ToHexString(reloaded.Ciphertext)).IsEqualTo("01020304");
        await Assert.That(storedKind).IsEqualTo("LlmApiKey");
    }

    [Test]
    public async Task EncryptedValue_PrimaryKeyIncludesOrganizationIdForFutureDistribution()
    {
        var primaryKeyColumns = await ExecuteScalarAsync<string>(
            """
            SELECT string_agg(attribute.attname, ',' ORDER BY key_column.ordinality)
            FROM pg_index index
            JOIN pg_class table_class ON table_class.oid = index.indrelid
            JOIN pg_namespace table_schema ON table_schema.oid = table_class.relnamespace
            JOIN unnest(index.indkey) WITH ORDINALITY AS key_column(attnum, ordinality) ON TRUE
            JOIN pg_attribute attribute
              ON attribute.attrelid = table_class.oid
             AND attribute.attnum = key_column.attnum
            WHERE table_schema.nspname = 'zeeq'
              AND table_class.relname = 'core_encrypted_values'
              AND index.indisprimary
            """
        );

        await Assert.That(primaryKeyColumns).IsEqualTo("organization_id,id");
    }

    private async Task<T> ExecuteScalarAsync<T>(
        string commandText,
        params (string Name, object Value)[] parameters
    )
    {
        var connection = _context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();

        return result is T typed
            ? typed
            : throw new InvalidOperationException(
                $"Expected scalar result of type {typeof(T).Name}."
            );
    }
}
