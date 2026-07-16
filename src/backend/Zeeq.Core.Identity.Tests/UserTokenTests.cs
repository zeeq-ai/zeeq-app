using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity.Tests;

public class UserTokenTests
{
    [Test]
    public async Task CreatePrincipal_PreservesOwnerIdentityAndLongLivedTokenClaims()
    {
        var owner = new ClaimsPrincipal(
            new ClaimsIdentity([
                new Claim(Claims.Subject, "usr_123"),
                new Claim(Claims.Name, "Test User"),
                new Claim(Claims.Email, "test@example.com"),
                new Claim(AuthClaims.OrganizationId, "org_123"),
                new Claim(AuthClaims.TeamId, "team_123"),
                new Claim(AuthClaims.PartitionIds, "[]"),
                new Claim(AuthClaims.Provider, "mock"),
                new Claim(AuthClaims.ProviderSubject, "123"),
            ])
        );
        var token = new UserToken
        {
            Id = "auth_tok_test",
            OwnerUserId = "usr_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            OwnerProvider = "mock",
            OwnerProviderSubject = "123",
            DisplayName = "Test Token",
            SelectedPartitionIdsJson = "[]",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(90),
        };

        var principal = UserTokenOpenIddictFactory.CreatePrincipal(
            owner,
            token,
            new AuthSettings { Resource = "http://localhost:8091/mcp/" }
        );

        await Assert.That(principal.GetClaim(Claims.Subject)).IsEqualTo("usr_123");
        await Assert.That(principal.GetClaim(Claims.Name)).IsEqualTo("Test User");
        await Assert.That(principal.GetClaim(Claims.Email)).IsEqualTo("test@example.com");
        await Assert.That(principal.GetClaim(AuthClaims.OrganizationId)).IsEqualTo("org_123");
        await Assert.That(principal.GetClaim(AuthClaims.TeamId)).IsEqualTo("team_123");
        await Assert.That(principal.GetClaim(AuthClaims.PartitionIds)).IsEqualTo("[]");
        await Assert.That(principal.GetClaim(AuthClaims.Provider)).IsEqualTo("mock");
        await Assert.That(principal.GetClaim(AuthClaims.ProviderSubject)).IsEqualTo("123");
        await Assert.That(principal.GetClaim(AuthClaims.AuthMode)).IsEqualTo("long_lived_token");
        await Assert.That(principal.GetClaim(AuthClaims.UserTokenId)).IsEqualTo("auth_tok_test");
        await Assert.That(principal.GetClaim(AuthClaims.UserTokenName)).IsEqualTo("Test Token");
        await Assert.That(principal.GetScopes()).IsEquivalentTo(["mcp:tools"]);
        await Assert.That(principal.GetResources()).IsEquivalentTo(["http://localhost:8091/mcp"]);
        await Assert.That(principal.GetAccessTokenLifetime()).IsNotNull();
    }

    [Test]
    public async Task AuthSettings_DefaultsToEncryptedJwtAccessTokens()
    {
        var settings = new AuthSettings();

        await Assert.That(settings.AccessTokenFormat).IsEqualTo(AccessTokenFormat.EncryptedJwt);
    }

    [Test]
    public async Task AuthSettings_AcceptsSignedJwtAccessTokens()
    {
        var settings = new AuthSettings { AccessTokenFormat = AccessTokenFormat.SignedJwt };

        await Assert.That(settings.AccessTokenFormat).IsEqualTo(AccessTokenFormat.SignedJwt);
    }

    [Test]
    public async Task InternalClientFactory_CreatesConfidentialCustomGrantApplication()
    {
        var settings = new AuthSettings
        {
            Resource = "http://localhost:8091/mcp/",
            InternalUserTokenClientId = "auth_internal_test",
            InternalUserTokenClientSecret = "test-secret",
        };

        var descriptor = UserTokenInternalClientFactory.CreateApplicationDescriptor(settings);

        await Assert.That(descriptor.ClientId).IsEqualTo("auth_internal_test");
        await Assert.That(descriptor.ClientSecret).IsEqualTo("test-secret");
        await Assert.That(descriptor.ClientType).IsEqualTo(ClientTypes.Confidential);
        await Assert.That(descriptor.Permissions).Contains(Permissions.Endpoints.Token);
        await Assert
            .That(descriptor.Permissions)
            .Contains(Permissions.Prefixes.Scope + "mcp:tools");
        await Assert
            .That(descriptor.Permissions)
            .Contains(Permissions.Prefixes.Resource + "http://localhost:8091/mcp");
        await Assert.That(descriptor.Permissions).Contains("gt:" + UserTokenGrantHandler.GrantType);
    }
}

public class OpenIddictResourceValidatorTests
{
    [Test]
    public async Task ValidateAuthorizationRequest_WithExpectedResource_AllowsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateAuthorizationRequest(
            ["http://localhost:8091/mcp"],
            "http://localhost:8091/mcp/"
        );

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task ValidateAuthorizationRequest_WithMissingResource_RejectsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateAuthorizationRequest(
            [],
            "http://localhost:8091/mcp"
        );

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Code).IsEqualTo("MissingResource");
    }

    [Test]
    public async Task ValidateAuthorizationRequest_WithMismatchedResource_RejectsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateAuthorizationRequest(
            ["http://localhost:8091/other"],
            "http://localhost:8091/mcp"
        );

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Code).IsEqualTo("InvalidResource");
    }

    [Test]
    public async Task ValidateTokenRequest_WithMissingNonRefreshResource_RejectsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateTokenRequest(
            GrantTypes.ClientCredentials,
            [],
            "http://localhost:8091/mcp"
        );

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Code).IsEqualTo("MissingResource");
    }

    [Test]
    public async Task ValidateTokenRequest_WithMissingRefreshResource_AllowsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateTokenRequest(
            GrantTypes.RefreshToken,
            [],
            "http://localhost:8091/mcp"
        );

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task ValidateTokenRequest_WithMismatchedRefreshResource_RejectsRequest()
    {
        var result = OpenIddictResourceValidator.ValidateTokenRequest(
            GrantTypes.RefreshToken,
            ["http://localhost:8091/other"],
            "http://localhost:8091/mcp"
        );

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Code).IsEqualTo("InvalidResource");
    }
}
