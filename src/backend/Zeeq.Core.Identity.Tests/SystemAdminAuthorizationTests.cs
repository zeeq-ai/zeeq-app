using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Zeeq.Core.Identity.Tests;

public sealed class SystemAdminAuthorizationTests
{
    [Test]
    public async Task SystemAdminAuthorizationHandler_WithConfiguredSubject_Succeeds()
    {
        var requirement = new SystemAdminRequirement();
        var handler = new SystemAdminAuthorizationHandler(
            new SystemAdminEvaluator(
                new AppSettings
                {
                    Platform = new PlatformSettings { SystemAdminSubjects = ["google:sub_123"] },
                }
            )
        );
        var context = new AuthorizationHandlerContext(
            [requirement],
            Principal("google", "sub_123"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task SystemAdminAuthorizationHandler_WithStaleRoleButUnconfiguredSubject_DoesNotSucceed()
    {
        var requirement = new SystemAdminRequirement();
        var handler = new SystemAdminAuthorizationHandler(
            new SystemAdminEvaluator(
                new AppSettings
                {
                    Platform = new PlatformSettings
                    {
                        SystemAdminSubjects = ["google:other-subject"],
                    },
                }
            )
        );
        var context = new AuthorizationHandlerContext(
            [requirement],
            Principal("google", "sub_123", [SystemRoles.SystemAdmin]),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task HiddenAdminAuthorizationResultHandler_WithHiddenAdminChallenge_ReturnsNotFound()
    {
        var context = HiddenAdminHttpContext();
        var nextCalled = false;

        await new HiddenAdminAuthorizationResultHandler().HandleAsync(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            context,
            Policy(),
            PolicyAuthorizationResult.Challenge()
        );

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status404NotFound);
        await Assert.That(nextCalled).IsFalse();
    }

    [Test]
    public async Task HiddenAdminAuthorizationResultHandler_WithHiddenAdminForbid_ReturnsNotFound()
    {
        var context = HiddenAdminHttpContext();
        var nextCalled = false;

        await new HiddenAdminAuthorizationResultHandler().HandleAsync(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            context,
            Policy(),
            PolicyAuthorizationResult.Forbid()
        );

        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status404NotFound);
        await Assert.That(nextCalled).IsFalse();
    }

    [Test]
    public async Task HiddenAdminAuthorizationResultHandler_WithHiddenAdminSuccess_InvokesNext()
    {
        var context = HiddenAdminHttpContext();
        var nextCalled = false;

        await new HiddenAdminAuthorizationResultHandler().HandleAsync(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            context,
            Policy(),
            PolicyAuthorizationResult.Success()
        );

        await Assert.That(nextCalled).IsTrue();
    }

    [Test]
    public async Task HiddenAdminAuthorizationResultHandler_WithNonAdminSuccess_DelegatesToDefault()
    {
        var context = new DefaultHttpContext();
        var nextCalled = false;

        await new HiddenAdminAuthorizationResultHandler().HandleAsync(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            context,
            Policy(),
            PolicyAuthorizationResult.Success()
        );

        await Assert.That(nextCalled).IsTrue();
    }

    private static ClaimsPrincipal Principal(
        string provider,
        string providerSubject,
        string[]? roles = null
    ) =>
        new(
            new ClaimsIdentity(
                BuildClaims(
                    [
                        new Claim(AuthClaims.Provider, provider),
                        new Claim(AuthClaims.ProviderSubject, providerSubject),
                    ],
                    roles
                ),
                authenticationType: "Test"
            )
        );

    private static IEnumerable<Claim> BuildClaims(Claim[] claims, string[]? roles)
    {
        foreach (var claim in claims)
        {
            yield return claim;
        }

        foreach (var role in roles ?? [])
        {
            yield return new Claim(ClaimTypes.Role, role);
        }
    }

    private static DefaultHttpContext HiddenAdminHttpContext()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new HiddenAdminRouteMetadata()),
                displayName: "Hidden admin endpoint"
            )
        );

        return context;
    }

    private static AuthorizationPolicy Policy() =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
}
