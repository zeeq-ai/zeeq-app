using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewDiffUploadTokenProtectorTests
{
    [Test]
    public async Task Protect_ThenTryUnprotect_RoundTripsPayload()
    {
        var protector = CreateProtector();
        var payload = CodeReviewDiffUploadTokenProtector.CreatePayload(
            jobId: "018ff6a5f6e57b06b4c1a0f9c13e0f12",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            createdById: "usr_123",
            organizationId: "org_123",
            traceContext: new ZeeqTraceContext(
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00",
                "state=1"
            )
        );

        var token = protector.Protect(payload);
        var success = protector.TryUnprotect(token, out var roundTripped);

        await Assert.That(success).IsTrue();
        await Assert.That(roundTripped).IsNotNull();
        await Assert.That(roundTripped!.JobId).IsEqualTo(payload.JobId);
        await Assert.That(roundTripped.OrganizationId).IsEqualTo("org_123");
        await Assert.That(roundTripped.TraceParent).IsEqualTo(payload.TraceParent);
        await Assert.That(roundTripped.TraceState).IsEqualTo(payload.TraceState);
    }

    [Test]
    public async Task TryUnprotect_WithWrongKey_ReturnsFalse()
    {
        var token = CreateProtector("first-key").Protect(ValidPayload());

        var success = CreateProtector("second-key").TryUnprotect(token, out var payload);

        await Assert.That(success).IsFalse();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task TryUnprotect_WithTamperedToken_ReturnsFalse()
    {
        var protector = CreateProtector();
        var token = protector.Protect(ValidPayload());
        // Change a middle character where all bits are real data bits
        // (last base64url character can have ignored padding bits when byte count % 3 == 1)
        var pos = token.Length / 2;
        var replacement = token[pos] == 'X' ? 'Y' : 'X';
        var tamperedToken = token[..pos] + replacement + token[(pos + 1)..];

        var success = protector.TryUnprotect(tamperedToken, out var payload);

        await Assert.That(success).IsFalse();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task TryUnprotect_WithExpiredToken_ReturnsFalse()
    {
        var protector = CreateProtector();
        var token = protector.Protect(
            ValidPayload(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1))
        );

        var success = protector.TryUnprotect(token, out var payload);

        await Assert.That(success).IsFalse();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task TryUnprotect_WithBlankRequiredFields_ReturnsFalse()
    {
        var protector = CreateProtector();
        var token = protector.Protect(ValidPayload(jobId: string.Empty));

        var success = protector.TryUnprotect(token, out var payload);

        await Assert.That(success).IsFalse();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task UploadTokens_AndRequestLinkTokens_AreNotCrossRedeemable()
    {
        var settings = new CodeReviewSettings
        {
            ReviewRequestLinkEncryptionKey = "shared-key",
            ReviewRequestLinkValidityDays = 7,
            DiffUploadUrlValidityMinutes = 30,
        };
        var uploadProtector = new CodeReviewDiffUploadTokenProtector(settings);
        var requestProtector = new CodeReviewRequestTokenProtector(settings);

        var uploadToken = uploadProtector.Protect(ValidPayload());
        var requestToken = requestProtector.ProtectInitialReview(
            organizationId: "org_123",
            teamId: null,
            repositoryId: "repo_123",
            ownerQualifiedRepoName: "wonderlydotcom/zeeq",
            pullRequestNumber: 42,
            remainingReviewBudget: 10,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(1)
        );

        await Assert.That(uploadProtector.TryUnprotect(requestToken, out _)).IsFalse();
        await Assert.That(requestProtector.TryUnprotect(uploadToken, out _)).IsFalse();
    }

    [Test]
    public async Task GetValidity_WithInvalidSetting_Throws()
    {
        var protector = new CodeReviewDiffUploadTokenProtector(
            new CodeReviewSettings
            {
                ReviewRequestLinkEncryptionKey = "test-key",
                DiffUploadUrlValidityMinutes = 0,
            }
        );

        await Assert.That(() => protector.GetValidity()).Throws<InvalidOperationException>();
    }

    private static CodeReviewDiffUploadTokenProtector CreateProtector(string key = "test-key") =>
        new(new CodeReviewSettings { ReviewRequestLinkEncryptionKey = key });

    private static CodeReviewDiffUploadTokenPayload ValidPayload(
        string jobId = "018ff6a5f6e57b06b4c1a0f9c13e0f12",
        DateTimeOffset? expiresAtUtc = null
    ) =>
        CodeReviewDiffUploadTokenProtector.CreatePayload(
            jobId: jobId,
            expiresAtUtc: expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(10),
            createdById: "usr_123",
            organizationId: "org_123"
        );
}
