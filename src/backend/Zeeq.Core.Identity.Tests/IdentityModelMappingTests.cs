using Zeeq.Core.Models;
using Zeeq.Data.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Wonderly.Zeeq.Tests;

public sealed class IdentityModelMappingTests
{
    [Test]
    public async Task SharedCoreModels_UseCoreTablePrefix()
    {
        using var context = CreateContext();
        var model = context.Model;

        await Assert
            .That(model.FindEntityType(typeof(User))?.GetTableName())
            .IsEqualTo("core_users");
        await Assert
            .That(model.FindEntityType(typeof(Organization))?.GetTableName())
            .IsEqualTo("core_organizations");
        await Assert
            .That(model.FindEntityType(typeof(Team))?.GetTableName())
            .IsEqualTo("core_teams");
        await Assert
            .That(model.FindEntityType(typeof(OrganizationMembership))?.GetTableName())
            .IsEqualTo("core_organization_memberships");
        await Assert
            .That(model.FindEntityType(typeof(TeamMembership))?.GetTableName())
            .IsEqualTo("core_team_memberships");
        await Assert
            .That(model.FindEntityType(typeof(Partition))?.GetTableName())
            .IsEqualTo("core_partitions");
    }

    [Test]
    public async Task AuthSpecificModels_UseAuthTablePrefix()
    {
        using var context = CreateContext();
        var model = context.Model;

        await Assert
            .That(model.FindEntityType(typeof(ExternalUserIdentity))?.GetTableName())
            .IsEqualTo("auth_user_identities");
        await Assert
            .That(model.FindEntityType(typeof(ClientCredential))?.GetTableName())
            .IsEqualTo("auth_client_credentials");
        await Assert
            .That(model.FindEntityType(typeof(DcrClientSetup))?.GetTableName())
            .IsEqualTo("auth_dcr_client_setups");
        await Assert
            .That(model.FindEntityType(typeof(UserToken))?.GetTableName())
            .IsEqualTo("auth_user_tokens");
        await Assert
            .That(model.FindEntityType(typeof(AuthTransientState))?.GetTableName())
            .IsEqualTo("auth_transient_states");
    }

    private static PostgresDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=zeeq_model_mapping_tests",
                npgsqlOptions => npgsqlOptions.UseVector()
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .Options;

        return new PostgresDbContext(options);
    }
}
