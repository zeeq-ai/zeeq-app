using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity.Tests;

public sealed class SystemAdminSubjectMatcherTests
{
    [Test]
    public async Task IsSystemAdminSubject_WithExactProviderSubject_ReturnsTrue()
    {
        var isAdmin = SystemAdminSubjectMatcher.IsSystemAdminSubject(
            provider: "google",
            subject: "Subject-123",
            configuredSubjects: ["google:Subject-123"]
        );

        await Assert.That(isAdmin).IsTrue();
    }

    [Test]
    public async Task IsSystemAdminSubject_WithProviderCaseDifference_ReturnsTrue()
    {
        var isAdmin = SystemAdminSubjectMatcher.IsSystemAdminSubject(
            provider: "Google",
            subject: "Subject-123",
            configuredSubjects: ["google:Subject-123"]
        );

        await Assert.That(isAdmin).IsTrue();
    }

    [Test]
    public async Task IsSystemAdminSubject_WithSubjectCaseDifference_ReturnsFalse()
    {
        var isAdmin = SystemAdminSubjectMatcher.IsSystemAdminSubject(
            provider: "google",
            subject: "subject-123",
            configuredSubjects: ["google:Subject-123"]
        );

        await Assert.That(isAdmin).IsFalse();
    }

    [Test]
    public async Task IsSystemAdminSubject_WithSameSubjectDifferentProvider_ReturnsFalse()
    {
        var isAdmin = SystemAdminSubjectMatcher.IsSystemAdminSubject(
            provider: "github",
            subject: "Subject-123",
            configuredSubjects: ["google:Subject-123"]
        );

        await Assert.That(isAdmin).IsFalse();
    }

    [Test]
    public async Task IsSystemAdminSubject_WithWhitespaceAndMalformedEntries_IgnoresInvalidValues()
    {
        var isAdmin = SystemAdminSubjectMatcher.IsSystemAdminSubject(
            provider: " google ",
            subject: " Subject-123 ",
            configuredSubjects:
            [
                null,
                "",
                "   ",
                "missing-separator",
                ":missing-provider",
                "missing-subject:",
                " google : Subject-123 ",
            ]
        );

        await Assert.That(isAdmin).IsTrue();
    }

    [Test]
    public async Task IsSystemAdminSubject_WithMissingProviderOrSubject_ReturnsFalse()
    {
        var configuredSubjects = new[] { "google:Subject-123" };

        await Assert
            .That(
                SystemAdminSubjectMatcher.IsSystemAdminSubject(
                    provider: null,
                    subject: "Subject-123",
                    configuredSubjects
                )
            )
            .IsFalse();
        await Assert
            .That(
                SystemAdminSubjectMatcher.IsSystemAdminSubject(
                    provider: "google",
                    subject: "",
                    configuredSubjects
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task SystemAdminEvaluator_WithMatchingEmailOnly_ReturnsFalse()
    {
        var evaluator = new SystemAdminEvaluator(
            new AppSettings
            {
                Platform = new PlatformSettings { SystemAdminSubjects = ["google:actual-subject"] },
            }
        );
        var principal = TestPrincipal(
            provider: "google",
            providerSubject: "different-subject",
            email: "operator@example.com"
        );

        await Assert.That(evaluator.IsSystemAdmin(principal)).IsFalse();
    }

    [Test]
    public async Task SystemAdminEvaluator_WithConfiguredProviderSubject_ReturnsTrue()
    {
        var evaluator = new SystemAdminEvaluator(
            new AppSettings
            {
                Platform = new PlatformSettings { SystemAdminSubjects = ["google:sub_123"] },
            }
        );
        var principal = TestPrincipal(
            provider: "google",
            providerSubject: "sub_123",
            email: "different@example.com"
        );

        await Assert.That(evaluator.IsSystemAdmin(principal)).IsTrue();
    }

    private static ClaimsPrincipal TestPrincipal(
        string provider,
        string providerSubject,
        string email
    ) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(AuthClaims.Provider, provider),
                    new Claim(AuthClaims.ProviderSubject, providerSubject),
                    new Claim(OpenIddictConstants.Claims.Email, email),
                ],
                authenticationType: "Test"
            )
        );
}
