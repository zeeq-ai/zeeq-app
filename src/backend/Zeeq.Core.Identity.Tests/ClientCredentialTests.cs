using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity.Tests;

public class ClientCredentialTests
{
    [Test]
    public async Task CreateApplicationDescriptor_CreatesConfidentialClientCredentialsApplication()
    {
        var settings = new AuthSettings { Resource = "http://localhost:8091/mcp/" };

        var descriptor = ClientCredentialOpenIddictFactory.CreateApplicationDescriptor(
            clientId: "auth_cred_test",
            clientSecret: "test-secret",
            displayName: "Test Credential",
            settings
        );

        await Assert.That(descriptor.ClientId).IsEqualTo("auth_cred_test");
        await Assert.That(descriptor.ClientSecret).IsEqualTo("test-secret");
        await Assert.That(descriptor.ClientType).IsEqualTo(ClientTypes.Confidential);
        await Assert.That(descriptor.ConsentType).IsEqualTo(ConsentTypes.Implicit);
        await Assert.That(descriptor.Permissions).Contains(Permissions.Endpoints.Token);
        await Assert
            .That(descriptor.Permissions)
            .Contains(Permissions.GrantTypes.ClientCredentials);
        await Assert
            .That(descriptor.Permissions)
            .Contains(Permissions.Prefixes.Scope + "mcp:tools");
        await Assert
            .That(descriptor.Permissions)
            .Contains(Permissions.Prefixes.Resource + "http://localhost:8091/mcp");
    }

    [Test]
    public async Task CreatePrincipal_PreservesOwnerIdentityForAccessToken()
    {
        var credential = new ClientCredential
        {
            ClientId = "auth_cred_test",
            ClientSecret = "test-secret",
            OwnerUserId = "usr_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            OwnerProvider = "mock",
            OwnerProviderSubject = "123",
            DisplayName = "Test Credential",
            SelectedPartitionIdsJson = "[]",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var principal = ClientCredentialOpenIddictFactory.CreatePrincipal(
            credential,
            new AuthSettings
            {
                Resource = "http://localhost:8091/mcp/",
                ClientCredentialsAccessTokenLifetime = TimeSpan.FromMinutes(10),
            },
            scopes: ["mcp:tools"]
        );

        await Assert.That(principal.GetClaim(Claims.Subject)).IsEqualTo("auth_cred_test");
        await Assert.That(principal.GetClaim(AuthClaims.OwnerUserId)).IsEqualTo("usr_123");
        await Assert.That(principal.GetClaim(AuthClaims.OrganizationId)).IsEqualTo("org_123");
        await Assert.That(principal.GetClaim(AuthClaims.TeamId)).IsEqualTo("team_123");
        await Assert.That(principal.GetClaim(AuthClaims.PartitionIds)).IsEqualTo("[]");
        await Assert.That(principal.GetClaim(AuthClaims.Provider)).IsEqualTo("mock");
        await Assert.That(principal.GetClaim(AuthClaims.ProviderSubject)).IsEqualTo("123");
        await Assert
            .That(principal.GetClaim(AuthClaims.AuthMode))
            .IsEqualTo(GrantTypes.ClientCredentials);
        await Assert
            .That(principal.GetClaim(AuthClaims.ClientCredentialId))
            .IsEqualTo("auth_cred_test");
        await Assert
            .That(principal.GetClaim(AuthClaims.ClientCredentialName))
            .IsEqualTo("Test Credential");
        await Assert.That(principal.GetScopes()).IsEquivalentTo(["mcp:tools"]);
        await Assert.That(principal.GetResources()).IsEquivalentTo(["http://localhost:8091/mcp"]);
        await Assert.That(principal.GetAccessTokenLifetime()).IsEqualTo(TimeSpan.FromMinutes(10));
    }
}
