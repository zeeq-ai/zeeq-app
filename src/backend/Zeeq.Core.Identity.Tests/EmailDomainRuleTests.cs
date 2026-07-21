using Zeeq.Core.Identity;

namespace Wonderly.Zeeq.Tests;

/// <summary>
/// Unit tests for email-domain extraction and public mailbox exclusions used by same-domain onboarding.
/// </summary>
public sealed class EmailDomainRuleTests
{
    [Test]
    [Arguments("owner@example.com", "example.com")]
    [Arguments("owner@example.com.", "example.com")]
    [Arguments("OWNER@Sub.Example.COM", "example.com")]
    [Arguments("owner@team.example.co.uk", "example.co.uk")]
    [Arguments("owner@engineering.example.gov.uk", "example.gov.uk")]
    [Arguments("owner@dept.example.com.au", "example.com.au")]
    [Arguments("owner@team.example.github.io", "example.github.io")]
    [Arguments("owner@dept.foo.co.id", "foo.co.id")]
    [Arguments("owner@dept.example.co.us", "example.co.us")]
    [Arguments("owner@dept.example.com.ua", "example.com.ua")]
    [Arguments("owner@team.co.fr", "co.fr")]
    public async Task FromEmail_WithValidEmail_ReturnsRegistrableDomain(
        string email,
        string expectedDomain
    )
    {
        var domain = EmailDomainNormalizer.FromEmail(email);

        await Assert.That(domain).IsEqualTo(expectedDomain);
    }

    [Test]
    [Arguments("")]
    [Arguments("owner")]
    [Arguments("@example.com")]
    [Arguments("owner@")]
    [Arguments("owner@@example.com")]
    [Arguments("owner@.example.com")]
    [Arguments("owner@example..com")]
    [Arguments("owner@example.com..")]
    [Arguments("owner@-example.com")]
    [Arguments("owner@example-.com")]
    [Arguments("owner@co.uk")]
    [Arguments("owner@co.id")]
    [Arguments("owner@co.us")]
    [Arguments("owner@github.io")]
    public async Task FromEmail_WithInvalidEmail_ReturnsNull(string email)
    {
        var domain = EmailDomainNormalizer.FromEmail(email);

        await Assert.That(domain).IsNull();
    }

    [Test]
    [Arguments("gmail.com")]
    [Arguments("GoogleMail.com")]
    [Arguments("qq.com")]
    [Arguments("mail.ru")]
    [Arguments("naver.com")]
    [Arguments("yahoo.com.au")]
    [Arguments("hotmail.co.uk")]
    [Arguments("uol.com.br")]
    public async Task IsPublicEmailDomain_WithKnownGlobalProvider_ReturnsTrue(string domain)
    {
        var isPublic = PublicEmailDomainCatalog.IsPublicEmailDomain(domain);

        await Assert.That(isPublic).IsTrue();
    }

    [Test]
    [Arguments("example.com")]
    [Arguments("acme.co.uk")]
    [Arguments("private-company.com.au")]
    public async Task IsPublicEmailDomain_WithPrivateDomain_ReturnsFalse(string domain)
    {
        var isPublic = PublicEmailDomainCatalog.IsPublicEmailDomain(domain);

        await Assert.That(isPublic).IsFalse();
    }
}
