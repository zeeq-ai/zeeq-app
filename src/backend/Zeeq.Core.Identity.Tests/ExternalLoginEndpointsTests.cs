using System.Reflection;
using Zeeq.Core.Common;

namespace Zeeq.Core.Identity.Tests;

public sealed class ExternalLoginEndpointsTests
{
    [Test]
    public async Task NormalizeReturnUrl_WithLocalRoot_ResolvesUnderFrontendBasePath()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("/", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithLocalPath_ResolvesUnderFrontendBasePath()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("/settings/github?tab=app", settings);

        await Assert
            .That(normalized)
            .IsEqualTo("https://app.zeeq.ai/web/settings/github?tab=app");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("/activate-organization", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithAbsoluteActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl(
            "https://app.zeeq.ai/web/activate-organization",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithInactiveOrgLoginPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("/login?inactiveOrg=true", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithAbsoluteInactiveOrgLoginPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl(
            "https://app.zeeq.ai/web/login?inactiveOrg=true",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithRetiredActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("/activate-account", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithRetiredAbsoluteActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl(
            "https://app.zeeq.ai/web/activate-account",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithUntrustedAbsoluteUrl_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = InvokeNormalizeReturnUrl("https://example.com/phish", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    private static string InvokeNormalizeReturnUrl(string? returnUrl, AuthSettings settings)
    {
        var method =
            typeof(ExternalLoginEndpoints).GetMethod(
                "NormalizeReturnUrl",
                BindingFlags.Static | BindingFlags.NonPublic
            )
            ?? throw new InvalidOperationException(
                "Could not find ExternalLoginEndpoints.NormalizeReturnUrl."
            );

        return (string)(
            method.Invoke(null, [returnUrl, settings])
            ?? throw new InvalidOperationException("NormalizeReturnUrl returned null.")
        );
    }
}
