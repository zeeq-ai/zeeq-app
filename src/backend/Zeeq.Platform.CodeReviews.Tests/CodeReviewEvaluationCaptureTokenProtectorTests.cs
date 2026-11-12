using Microsoft.Extensions.Time.Testing;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Unit tests for the signed evaluation capture token.
/// </summary>
/// <remarks>
/// The token is the only carrier of a staged capture, so binding and expiry are security
/// properties, not conveniences. Expiry uses the injected clock, so these tests never sleep.
/// </remarks>
public sealed class CodeReviewEvaluationCaptureTokenProtectorTests
{
    private static readonly CodeReviewSettings Settings = new()
    {
        ReviewRequestLinkEncryptionKey = "unit-test-evaluation-capture-key",
    };

    [Test]
    public async Task ProtectAndUnprotect_RoundTripsEveryBindingField()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var protector = new CodeReviewEvaluationCaptureTokenProtector(Settings, clock);
        var capture = CreateCapture();

        var payload = protector.CreatePayload("org_1", "repo_1", "dataset_1", 7, capture);
        var token = protector.Protect(payload);

        await Assert.That(protector.TryUnprotect(token, out var restored)).IsTrue();
        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.OrganizationId).IsEqualTo("org_1");
        await Assert.That(restored.RepositoryId).IsEqualTo("repo_1");
        await Assert.That(restored.DatasetId).IsEqualTo("dataset_1");
        await Assert.That(restored.ExpectedDatasetVersion).IsEqualTo(7);
        await Assert.That(restored.Capture.Snapshot.Sha256).IsEqualTo(capture.Snapshot.Sha256);
        await Assert.That(restored.Capture.BaseSha).IsEqualTo(capture.BaseSha);
        await Assert.That(restored.ExpiresAtUtc).IsEqualTo(payload.ExpiresAtUtc);
    }

    [Test]
    public async Task TryUnprotect_RejectsExpiredTokenButDistinguishesExpiryFromTampering()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var protector = new CodeReviewEvaluationCaptureTokenProtector(Settings, clock);
        var token = protector.Protect(
            protector.CreatePayload("org_1", "repo_1", "dataset_1", 1, CreateCapture())
        );

        clock.Advance(CodeReviewEvaluationLimits.CaptureTokenLifetime + TimeSpan.FromSeconds(1));

        await Assert.That(protector.TryUnprotect(token, out _)).IsFalse();
        await Assert.That(protector.TryUnprotectIgnoringExpiry(token, out var payload)).IsTrue();
        await Assert.That(payload).IsNotNull();

        await Assert.That(protector.TryUnprotectIgnoringExpiry(token + "x", out _)).IsFalse();
    }

    [Test]
    public async Task TryUnprotect_RejectsTamperedAndForeignTokens()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var protector = new CodeReviewEvaluationCaptureTokenProtector(Settings, clock);
        var payload = protector.CreatePayload("org_1", "repo_1", "dataset_1", 3, CreateCapture());
        var token = protector.Protect(payload);

        var flipped = token.ToCharArray();
        flipped[^3] = flipped[^3] == 'A' ? 'B' : 'A';

        await Assert.That(protector.TryUnprotect(new string(flipped), out _)).IsFalse();

        var otherKey = new CodeReviewEvaluationCaptureTokenProtector(
            new CodeReviewSettings { ReviewRequestLinkEncryptionKey = "another-key" },
            clock
        );
        await Assert.That(otherKey.TryUnprotect(token, out _)).IsFalse();
        await Assert.That(protector.TryUnprotect(null, out _)).IsFalse();
        await Assert.That(protector.TryUnprotect("not-a-token", out _)).IsFalse();
    }

    [Test]
    public async Task CreatePayload_UsesTheStandardFifteenMinuteLifetime()
    {
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var protector = new CodeReviewEvaluationCaptureTokenProtector(Settings, clock);

        var payload = protector.CreatePayload("org_1", "repo_1", "dataset_1", 1, CreateCapture());

        await Assert.That(payload.ExpiresAtUtc).IsEqualTo(now.AddMinutes(15));
    }

    private static CodeReviewEvaluationStagedCapture CreateCapture() =>
        new(
            new CodeReviewEvaluationArtifact(
                "postgres://reviewer-evaluations/org_1/repo_1/abc/snapshot.json",
                new string('a', 64),
                512
            ),
            901,
            "Fixture pull request",
            "octocat",
            "base-sha",
            "head-sha",
            PullRequestState.Open,
            new DateTimeOffset(2026, 11, 12, 9, 59, 0, TimeSpan.Zero),
            ChangedFileCount: 2,
            OmittedFileCount: 0,
            DeclaredChangedFileCount: 2,
            PullRequestRecordId: null
        );
}
