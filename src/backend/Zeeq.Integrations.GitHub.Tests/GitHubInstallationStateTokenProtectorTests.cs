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
        // Change a middle character where all bits are real data bits
        // (last base64url character can have ignored padding bits when byte count % 3 == 1)
        var pos = token.Length / 2;
        var tampered = token[..pos] + 'X' + token[(pos + 1)..];
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
