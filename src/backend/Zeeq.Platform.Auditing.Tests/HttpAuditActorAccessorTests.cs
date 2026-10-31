using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Zeeq.Core.Auditing;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Auditing.Tests;

/// <summary>
/// Guards actor resolution from server-issued claims: credential, human owner, channel, and
/// system-admin authority stay distinct.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Auditing.Tests --output detailed --disable-logo
/// </summary>
public sealed class HttpAuditActorAccessorTests
{
    private const string AdminProvider = "google";
    private const string AdminSubject = "sub-admin";

    private static HttpAuditActorAccessor Accessor(
        HttpContext? http,
        AuditActorContext? explicitContext = null
    ) =>
        new(
            new HttpContextAccessor { HttpContext = http },
            explicitContext ?? new AuditActorContext(),
            new SystemAdminEvaluator(
                new AppSettings
                {
                    Platform = new PlatformSettings
                    {
                        SystemAdminSubjects = [$"{AdminProvider}:{AdminSubject}"],
                    },
                }
            )
        );

    private static DefaultHttpContext Http(
        string path,
        IEnumerable<Claim>? claims,
        bool adminRoute = false
    )
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;

        if (claims is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        if (adminRoute)
        {
            http.SetEndpoint(
                new Endpoint(
                    requestDelegate: null,
                    new EndpointMetadataCollection(new HiddenAdminRouteMetadata()),
                    "admin"
                )
            );
        }

        return http;
    }

    private static List<Claim> UserClaims(string sub = "user_1") =>
        [
            new("sub", sub),
            new("name", "Alice"),
            new("email", "alice@example.com"),
            new(AuthClaims.OrganizationId, "org_1"),
            new(AuthClaims.Provider, "google"),
            new(AuthClaims.ProviderSubject, "sub-alice"),
        ];

    [Test]
    public async Task BrowserUser_IsUserViaWeb_WithDisplayName()
    {
        var actor = Accessor(Http("/api/v1/libraries", UserClaims())).Current;

        await Assert.That(actor.Kind).IsEqualTo(AuditActorKinds.User);
        await Assert.That(actor.Channel).IsEqualTo(AuditChannels.Web);
        await Assert.That(actor.Id).IsEqualTo("user_1");
        await Assert.That(actor.DisplayName).IsEqualTo("Alice");
        await Assert.That(actor.ResponsibleUserId).IsEqualTo("user_1");
        await Assert.That(actor.CredentialId).IsNull();
    }

    [Test]
    public async Task ClientCredential_UsesClientIdAsActor_AndKeepsHumanOwnerSeparate()
    {
        // The OAuth subject is the client id; owner_user_id is the human, never the actor id.
        var claims = new List<Claim>
        {
            new("sub", "client_abc"),
            new(AuthClaims.OwnerUserId, "human_9"),
            new(AuthClaims.OrganizationId, "org_1"),
            new(AuthClaims.AuthMode, "client_credentials"),
            new(AuthClaims.ClientCredentialId, "client_abc"),
            new(AuthClaims.ClientCredentialName, "CI pipeline"),
        };

        var actor = Accessor(Http("/mcp", claims)).Current;

        await Assert.That(actor.Kind).IsEqualTo(AuditActorKinds.ClientCredential);
        await Assert.That(actor.Id).IsEqualTo("client_abc");
        await Assert.That(actor.ResponsibleUserId).IsEqualTo("human_9");
        await Assert.That(actor.CredentialName).IsEqualTo("CI pipeline");
        await Assert.That(actor.DisplayName).IsEqualTo("CI pipeline");
        await Assert.That(actor.Channel).IsEqualTo(AuditChannels.Mcp);
    }

    [Test]
    public async Task UserToken_IsHumanActor_WithTokenAsCredential()
    {
        var claims = UserClaims();
        claims.Add(new(AuthClaims.AuthMode, "long_lived_token"));
        claims.Add(new(AuthClaims.UserTokenId, "tok_1"));
        claims.Add(new(AuthClaims.UserTokenName, "laptop"));

        var actor = Accessor(Http("/api/v1/libraries", claims)).Current;

        await Assert.That(actor.Kind).IsEqualTo(AuditActorKinds.UserToken);
        await Assert.That(actor.Id).IsEqualTo("user_1");
        await Assert.That(actor.CredentialId).IsEqualTo("tok_1");
        await Assert.That(actor.Channel).IsEqualTo(AuditChannels.Api);
    }

    [Test]
    public async Task SystemAdmin_OnlyWhenAdminRouteAndLiveAuthorityBothHold()
    {
        var adminClaims = UserClaims("admin_1");
        adminClaims.RemoveAll(c => c.Type is AuthClaims.Provider or AuthClaims.ProviderSubject);
        adminClaims.Add(new(AuthClaims.Provider, AdminProvider));
        adminClaims.Add(new(AuthClaims.ProviderSubject, AdminSubject));

        var onAdminRoute = Accessor(
            Http("/api/v1/admin/organizations/x", adminClaims, adminRoute: true)
        ).Current;
        var onNormalRoute = Accessor(Http("/api/v1/libraries", adminClaims)).Current;
        var notAdminOnAdminRoute = Accessor(
            Http("/api/v1/admin/x", UserClaims(), adminRoute: true)
        ).Current;

        await Assert.That(onAdminRoute.Kind).IsEqualTo(AuditActorKinds.SystemAdmin);
        await Assert.That(onAdminRoute.Channel).IsEqualTo(AuditChannels.SystemConsole);
        await Assert.That(onAdminRoute.Id).IsEqualTo("admin_1");
        await Assert.That(onNormalRoute.Kind).IsEqualTo(AuditActorKinds.User);
        await Assert.That(notAdminOnAdminRoute.Kind).IsEqualTo(AuditActorKinds.User);
    }

    [Test]
    public async Task NoHttpContext_UsesExplicitBackgroundActor()
    {
        var explicitContext = new AuditActorContext
        {
            Current = AuditActor.Worker("repository-sync"),
        };

        var actor = Accessor(http: null, explicitContext).Current;

        await Assert.That(actor.Kind).IsEqualTo(AuditActorKinds.System);
        await Assert.That(actor.Id).IsEqualTo("repository-sync");
        await Assert.That(actor.Channel).IsEqualTo(AuditChannels.Worker);
    }

    [Test]
    public async Task UnauthenticatedRequest_NeverInventsAHumanActor()
    {
        var actor = Accessor(Http("/webhooks/github", claims: null)).Current;

        await Assert.That(actor.Kind).IsEqualTo(AuditActorKinds.System);
        await Assert.That(actor.ResponsibleUserId).IsNull();
    }

    [Test]
    public async Task OperationId_IsStablePerScope()
    {
        var http = Http("/api/v1/x", UserClaims());
        var accessor = Accessor(http);

        await Assert.That(accessor.OperationId).IsEqualTo(accessor.OperationId);
    }
}
