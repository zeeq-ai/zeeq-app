using System.Buffers.Binary;
using System.Buffers.Text;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Unit tests for the compact single-review deep-link token.
///
/// Run with:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewSingleViewTokenTests/*"
/// </summary>
public sealed class CodeReviewSingleViewTokenTests
{
    [Test]
    [Arguments(CodeReviewSingleViewMode.Pr)]
    [Arguments(CodeReviewSingleViewMode.Agent)]
    public async Task Encode_ThenDecode_RoundTripsExactly(CodeReviewSingleViewMode mode)
    {
        // A value carrying full 100ns tick precision (the trailing 0 is the sub-microsecond tick).
        var createdAtUtc = new DateTimeOffset(2026, 7, 4, 19, 6, 12, TimeSpan.Zero).AddTicks(
            5831850
        );

        var token = CodeReviewSingleViewToken.Encode(createdAtUtc, mode);
        var decoded = CodeReviewSingleViewToken.TryDecode(
            token,
            out var createdBack,
            out var modeBack
        );

        await Assert.That(decoded).IsTrue();
        await Assert.That(createdBack.UtcTicks).IsEqualTo(createdAtUtc.UtcTicks);
        await Assert.That(modeBack).IsEqualTo(mode);
    }

    [Test]
    public async Task Encode_ProducesCompactUrlSafeToken()
    {
        var token = CodeReviewSingleViewToken.Encode(
            DateTimeOffset.UtcNow,
            CodeReviewSingleViewMode.Agent
        );

        // Base64Url of a 9-byte payload: 12 chars, none requiring URL escaping.
        await Assert.That(token.Length).IsEqualTo(12);
        await Assert.That(token).DoesNotContain("+");
        await Assert.That(token).DoesNotContain("/");
        await Assert.That(token).DoesNotContain("=");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("!!!not-base64!!!")]
    [Arguments("AAAA")] // valid base64url but wrong payload length
    public async Task TryDecode_WithInvalidInput_ReturnsFalse(string? token)
    {
        var decoded = CodeReviewSingleViewToken.TryDecode(token, out _, out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    public async Task TryDecode_WithUndefinedModeByte_ReturnsFalse()
    {
        // Craft a well-formed 9-byte payload whose mode byte is not a defined enum value.
        Span<byte> payload = stackalloc byte[9];
        BinaryPrimitives.WriteInt64BigEndian(payload, DateTimeOffset.UtcNow.UtcTicks);
        payload[8] = 99;
        var token = Base64Url.EncodeToString(payload);

        var decoded = CodeReviewSingleViewToken.TryDecode(token, out _, out _);

        await Assert.That(decoded).IsFalse();
    }
}
