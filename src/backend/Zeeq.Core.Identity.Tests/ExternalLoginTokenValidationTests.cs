using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Zeeq.Core.Common;

namespace Zeeq.Core.Identity.Tests;

public sealed class ExternalLoginTokenValidationTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string MicrosoftIssuerTemplate =
        "https://login.microsoftonline.com/{tenantid}/v2.0";

    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("zeeq-microsoft-login-test-key-32")
    );

    [Test]
    [Arguments("11111111-1111-1111-1111-111111111111")]
    [Arguments("22222222-2222-2222-2222-222222222222")]
    public async Task MicrosoftIssuerValidator_AcceptsConcreteTenantIssuer(string tenantId)
    {
        var token = CreateIdToken(
            issuer: MicrosoftIssuer(tenantId),
            tenantId: tenantId,
            objectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        );

        var principal = ValidateToken(MicrosoftProvider(), MicrosoftConfiguration(), token);

        await Assert.That(principal.FindFirstValue("tid")).IsEqualTo(tenantId);
    }

    [Test]
    public async Task MicrosoftIssuerValidator_RejectsIssuerTenantMismatch()
    {
        var token = CreateIdToken(
            issuer: MicrosoftIssuer("11111111-1111-1111-1111-111111111111"),
            tenantId: "22222222-2222-2222-2222-222222222222",
            objectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        );

        void Act() => ValidateToken(MicrosoftProvider(), MicrosoftConfiguration(), token, true);

        await Assert.That(Act).Throws<SecurityTokenInvalidIssuerException>();
    }

    [Test]
    public async Task MicrosoftIssuerValidator_RejectsNonMicrosoftIssuer()
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var token = CreateIdToken(
            issuer: $"https://example.com/{tenantId}/v2.0",
            tenantId: tenantId,
            objectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        );

        void Act() => ValidateToken(MicrosoftProvider(), MicrosoftConfiguration(), token, true);

        await Assert.That(Act).Throws<SecurityTokenInvalidIssuerException>();
    }

    [Test]
    public async Task MicrosoftIssuerValidator_DoesNotWeakenAudienceValidation()
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var token = CreateIdToken(
            issuer: MicrosoftIssuer(tenantId),
            tenantId: tenantId,
            objectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            audience: "wrong-client"
        );

        void Act() => ValidateToken(MicrosoftProvider(), MicrosoftConfiguration(), token);

        await Assert.That(Act).Throws<SecurityTokenInvalidAudienceException>();
    }

    [Test]
    public async Task NonMicrosoftProvider_RequiresExactDiscoveredIssuer()
    {
        var provider = new ProviderAuthSettings
        {
            Name = "google",
            ClientId = ClientId,
            IssuerUri = "https://accounts.google.com",
        };
        var configuration = Configuration("https://accounts.google.com");
        var token = CreateIdToken(
            issuer: "https://attacker.example",
            tenantId: null,
            objectId: null
        );

        void Act() => ValidateToken(provider, configuration, token);

        await Assert.That(Act).Throws<SecurityTokenInvalidIssuerException>();
    }

    [Test]
    public async Task GetProviderSubject_ForMicrosoft_UsesCanonicalTenantAndObjectIds()
    {
        var principal = Principal(
            new Claim("tid", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            new Claim("oid", "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
            new Claim(JwtRegisteredClaimNames.Sub, "ignored-subject")
        );

        var subject = ExternalLoginEndpoints.GetProviderSubject(MicrosoftProvider(), principal);

        await Assert
            .That(subject)
            .IsEqualTo("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    }

    [Test]
    public async Task GetProviderSubject_ForSameObjectInDifferentTenants_ReturnsDifferentSubjects()
    {
        var first = ExternalLoginEndpoints.GetProviderSubject(
            MicrosoftProvider(),
            Principal(
                new Claim("tid", "11111111-1111-1111-1111-111111111111"),
                new Claim("oid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            )
        );
        var second = ExternalLoginEndpoints.GetProviderSubject(
            MicrosoftProvider(),
            Principal(
                new Claim("tid", "22222222-2222-2222-2222-222222222222"),
                new Claim("oid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            )
        );

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    [Arguments("tid", null)]
    [Arguments("tid", "not-a-guid")]
    [Arguments("oid", null)]
    [Arguments("oid", "not-a-guid")]
    public async Task GetProviderSubject_ForInvalidMicrosoftIdentityClaim_Throws(
        string claimType,
        string? claimValue
    )
    {
        var claims = new List<Claim>();
        if (claimType != "tid")
        {
            claims.Add(new Claim("tid", "11111111-1111-1111-1111-111111111111"));
        }
        if (claimType != "oid")
        {
            claims.Add(new Claim("oid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        }
        if (claimValue is not null)
        {
            claims.Add(new Claim(claimType, claimValue));
        }

        void Act() =>
            ExternalLoginEndpoints.GetProviderSubject(MicrosoftProvider(), Principal([.. claims]));

        await Assert.That(Act).Throws<SecurityTokenValidationException>();
    }

    [Test]
    public async Task GetProviderSubject_ForNonMicrosoftProvider_PreservesSubject()
    {
        var provider = new ProviderAuthSettings { Name = "google" };
        var principal = Principal(new Claim(JwtRegisteredClaimNames.Sub, "google-subject"));

        var subject = ExternalLoginEndpoints.GetProviderSubject(provider, principal);

        await Assert.That(subject).IsEqualTo("google-subject");
    }

    private static ClaimsPrincipal ValidateToken(
        ProviderAuthSettings provider,
        OpenIdConnectConfiguration configuration,
        string token,
        bool validateWithLkg = false
    )
    {
        var parameters = ExternalLoginEndpoints.CreateIdTokenValidationParameters(
            provider,
            configuration
        );
        parameters.ValidateWithLKG = validateWithLkg;

        return new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            token,
            parameters,
            out _
        );
    }

    private static string CreateIdToken(
        string issuer,
        string? tenantId,
        string? objectId,
        string audience = ClientId
    )
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "provider-subject") };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tid", tenantId));
        }
        if (objectId is not null)
        {
            claims.Add(new Claim("oid", objectId));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ProviderAuthSettings MicrosoftProvider() =>
        new()
        {
            Name = "microsoft",
            ClientId = ClientId,
            IssuerUri = "https://login.microsoftonline.com/common/v2.0",
        };

    private static OpenIdConnectConfiguration MicrosoftConfiguration() =>
        Configuration(MicrosoftIssuerTemplate);

    private static OpenIdConnectConfiguration Configuration(string issuer)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(SigningKey);
        return configuration;
    }

    private static string MicrosoftIssuer(string tenantId) =>
        $"https://login.microsoftonline.com/{tenantId}/v2.0";

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims));
}
