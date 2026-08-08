using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Integrations.Notion.Tests;

public sealed class NotionWebhookSignatureVerifierTests
{
    private readonly NotionWebhookSignatureVerifier _verifier = new();

    [Test]
    public async Task IsValid_AcceptsHmacOverExactRawBody()
    {
        const string secret = "verification-secret";
        var rawBody = Encoding.UTF8.GetBytes("{\n  \"type\": \"page.created\"\n}");
        var signature =
            "sha256="
            + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody)
            );

        var valid = _verifier.IsValid(rawBody, secret, signature);
        var reformattedValid = _verifier.IsValid(
            Encoding.UTF8.GetBytes("{\"type\":\"page.created\"}"),
            secret,
            signature
        );

        await Assert.That(valid).IsTrue();
        await Assert.That(reformattedValid).IsFalse();
    }

    [Arguments(null)]
    [Arguments("")]
    [Arguments("sha256=abcd")]
    [Arguments("SHA256=0000000000000000000000000000000000000000000000000000000000000000")]
    [Arguments("sha256=zz00000000000000000000000000000000000000000000000000000000000000")]
    [Test]
    public async Task IsValid_RejectsMissingOrMalformedHeader(string? signature)
    {
        var valid = _verifier.IsValid([1, 2, 3], "secret", signature);

        await Assert.That(valid).IsFalse();
    }
}
