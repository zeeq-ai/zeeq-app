using Microsoft.IdentityModel.Tokens;
using Zeeq.Core.Common;
using Zeeq.Integrations.GitHub;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubInstallationStateTokenProtectorTests
{
    [Test]
    public async Task Protect_ThenUnprotect_ReturnsPayload()
    {
        var protector = CreateProtector();
        var payload = new GitHubInstallationStatePayload(
            OrganizationId: "org_123",
            TeamId: "team_123",
            UserId: "user_123",
            Nonce: "nonce",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5)
        );

        var token = protector.Protect(payload);
        var valid = protector.TryUnprotect(token, out var restored);

        await Assert.That(valid).IsTrue();
        await Assert.That(restored).IsEqualTo(payload);
    }

    [Test]
    public async Task TryUnprotect_WithTamperedToken_ReturnsFalse()
    {
        var protector = CreateProtector();
        var payload = new GitHubInstallationStatePayload(
            OrganizationId: "org_123",
            TeamId: null,
            UserId: "user_123",
            Nonce: "nonce",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5)
        );

        var token = protector.Protect(payload);
        var tokenBytes = Base64UrlEncoder.DecodeBytes(token);
        tokenBytes[^1] ^= 0x01;
        var tampered = Base64UrlEncoder.Encode(tokenBytes);
        var valid = protector.TryUnprotect(tampered, out var restored);

        await Assert.That(valid).IsFalse();
        await Assert.That(restored).IsNull();
    }

    [Test]
    public async Task TryUnprotect_WithExpiredToken_ReturnsFalse()
    {
        var protector = CreateProtector();
        var payload = new GitHubInstallationStatePayload(
            OrganizationId: "org_123",
            TeamId: null,
            UserId: "user_123",
            Nonce: "nonce",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1)
        );

        var token = protector.Protect(payload);
        var valid = protector.TryUnprotect(token, out var restored);

        await Assert.That(valid).IsFalse();
        await Assert.That(restored).IsNull();
    }

    private static GitHubInstallationStateTokenProtector CreateProtector() =>
        new(new GitHubSettings { PrivateKeyPem = "test-state-protection-secret" });
}
