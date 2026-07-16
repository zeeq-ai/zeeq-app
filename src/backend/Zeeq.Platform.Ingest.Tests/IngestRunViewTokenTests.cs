using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="IngestRunViewToken"/> encode/decode round-trip.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/IngestRunViewTokenTests/*"
/// </summary>
public sealed class IngestRunViewTokenTests
{
    [Test]
    public async Task EncodeThenDecode_Public_RoundTrips()
    {
        var createdAt = DateTimeOffset.UtcNow;

        var token = IngestRunViewToken.Encode(createdAt, RepositorySourceKind.Public);
        var decoded = IngestRunViewToken.TryDecode(token, out var decodedCreatedAt, out var kind);

        await Assert.That(decoded).IsTrue();
        await Assert.That(decodedCreatedAt.UtcTicks).IsEqualTo(createdAt.UtcTicks);
        await Assert.That(kind).IsEqualTo(RepositorySourceKind.Public);
    }

    [Test]
    public async Task EncodeThenDecode_Private_RoundTrips()
    {
        var createdAt = DateTimeOffset.UtcNow;

        var token = IngestRunViewToken.Encode(createdAt, RepositorySourceKind.Private);
        var decoded = IngestRunViewToken.TryDecode(token, out var decodedCreatedAt, out var kind);

        await Assert.That(decoded).IsTrue();
        await Assert.That(decodedCreatedAt.UtcTicks).IsEqualTo(createdAt.UtcTicks);
        await Assert.That(kind).IsEqualTo(RepositorySourceKind.Private);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-valid-base64url!!!")]
    [Arguments("dG9vLXNob3J0")]
    public async Task TryDecode_InvalidInput_ReturnsFalse(string? token)
    {
        var decoded = IngestRunViewToken.TryDecode(token, out _, out _);

        await Assert.That(decoded).IsFalse();
    }
}
