using Zeeq.Core.Common;

namespace Zeeq.Core.Identity.Tests;

public sealed class ExternalLoginEndpointsTests
{
    [Test]
    public async Task NormalizeReturnUrl_WithLocalRoot_ResolvesUnderFrontendBasePath()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithLocalPath_ResolvesUnderFrontendBasePath()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/settings/github?tab=app", settings);

        await Assert
            .That(normalized)
            .IsEqualTo("https://app.zeeq.ai/web/settings/github?tab=app");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/activate-organization", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithAbsoluteActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl(
            "https://app.zeeq.ai/web/activate-organization",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithInactiveOrgLoginPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/login?inactiveOrg=true", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithAbsoluteInactiveOrgLoginPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl(
            "https://app.zeeq.ai/web/login?inactiveOrg=true",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithRetiredActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/activate-account", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithRetiredAbsoluteActivationPath_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl(
            "https://app.zeeq.ai/web/activate-account",
            settings
        );

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    [Test]
    public async Task NormalizeReturnUrl_WithUntrustedAbsoluteUrl_FallsBackToFrontendRoot()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("https://example.com/phish", settings);

        await Assert.That(normalized).IsEqualTo("https://app.zeeq.ai/web/");
    }

    /// <summary>
    /// Verifies the inactive-organization notice keeps the frontend base path.
    /// </summary>
    /// <remarks>
    /// Production serves the app under <c>/web/</c>, so the callback must redirect to
    /// <c>https://app.zeeq.ai/web/login?inactiveOrg=true</c> — the same target
    /// <c>RequireActiveCurrentOrganizationFilter</c> emits. This deliberately bypasses
    /// <c>NormalizeReturnUrl</c>, which collapses that path to the frontend root.
    /// </remarks>
    [Test]
    public async Task BuildInactiveOrganizationUrl_ResolvesUnderFrontendBasePath()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var url = ExternalLoginEndpoints.BuildInactiveOrganizationUrl(settings);

        await Assert.That(url).IsEqualTo("https://app.zeeq.ai/web/login?inactiveOrg=true");
    }

    /// <summary>
    /// Documents that <c>NormalizeReturnUrl</c> still collapses the notice path, which is
    /// why the callback must not route the inactive-org redirect through it.
    /// </summary>
    [Test]
    public async Task BuildInactiveOrganizationUrl_DiffersFromNormalizeReturnUrl()
    {
        var settings = new AuthSettings { FrontendBaseUri = "https://app.zeeq.ai/web" };

        var built = ExternalLoginEndpoints.BuildInactiveOrganizationUrl(settings);
        var normalized = ExternalLoginEndpoints.NormalizeReturnUrl("/login?inactiveOrg=true", settings);

        await Assert.That(built).IsNotEqualTo(normalized);
    }

}
