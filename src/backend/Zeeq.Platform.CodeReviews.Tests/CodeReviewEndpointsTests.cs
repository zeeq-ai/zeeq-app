namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEndpointsTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task TryDecodeSingleReviewToken_WithMissingToken_ReturnsMissingTokenError(
        string? token
    )
    {
        var success = CodeReviewEndpoints.TryDecodeSingleReviewToken(
            token,
            out _,
            out _,
            out var error
        );

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo("missing_token");
    }

    [Test]
    public async Task TryDecodeSingleReviewToken_WithMalformedToken_ReturnsInvalidTokenError()
    {
        var success = CodeReviewEndpoints.TryDecodeSingleReviewToken(
            "not-a-token",
            out _,
            out _,
            out var error
        );

        await Assert.That(success).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo("invalid_token");
    }

    [Test]
    public async Task TryDecodeSingleReviewToken_WithValidToken_ReturnsDecodedContractValues()
    {
        var createdAtUtc = new DateTimeOffset(2026, 7, 4, 19, 6, 12, TimeSpan.Zero);
        var token = CodeReviewSingleViewToken.Encode(createdAtUtc, CodeReviewSingleViewMode.Agent);

        var success = CodeReviewEndpoints.TryDecodeSingleReviewToken(
            token,
            out var decodedCreatedAtUtc,
            out var decodedMode,
            out var error
        );

        await Assert.That(success).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(decodedCreatedAtUtc.UtcTicks).IsEqualTo(createdAtUtc.UtcTicks);
        await Assert.That(decodedMode).IsEqualTo(CodeReviewSingleViewMode.Agent);
    }
}
