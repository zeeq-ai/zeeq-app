using Microsoft.AspNetCore.DataProtection;

namespace Zeeq.Integrations.Notion.Tests;

public sealed class NotionCallbackTokenProtectorTests
{
    private readonly NotionCallbackTokenProtector _protector = new(
        new EphemeralDataProtectionProvider()
    );

    [Test]
    public async Task Protect_RoundTripsPayload()
    {
        var expected = new NotionCallbackPayload("org-1", "library-1", 7);

        var token = _protector.Protect(expected);
        var valid = _protector.TryUnprotect(token, out var actual);

        await Assert.That(valid).IsTrue();
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(token).DoesNotContain("+");
        await Assert.That(token).DoesNotContain("/");
        await Assert.That(token).DoesNotContain("=");
    }

    [Test]
    public async Task TryUnprotect_RejectsTamperingAndDifferentPurposeKeyRing()
    {
        var token = _protector.Protect(new NotionCallbackPayload("org-1", "library-1", 7));
        var tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");
        var otherProtector = new NotionCallbackTokenProtector(
            new EphemeralDataProtectionProvider()
        );

        var tamperedValid = _protector.TryUnprotect(tampered, out _);
        var otherKeyRingValid = otherProtector.TryUnprotect(token, out _);

        await Assert.That(tamperedValid).IsFalse();
        await Assert.That(otherKeyRingValid).IsFalse();
    }
}
