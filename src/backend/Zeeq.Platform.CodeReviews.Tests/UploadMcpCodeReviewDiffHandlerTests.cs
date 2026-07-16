using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Common.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class UploadMcpCodeReviewDiffHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidDiff_StoresDiffAndReturnsOk()
    {
        var fixture = Fixture.Create();
        var diff = """
            diff --git a/src/App.cs b/src/App.cs
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -1 +1 @@
            -old
            +new
            """;

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor(diff),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewMcpDiffUploadResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.JobId).IsEqualTo(fixture.JobId);
        await Assert.That(ok.Value.ByteCount).IsEqualTo(Encoding.UTF8.GetByteCount(diff));
        await fixture
            .Storage.Received(1)
            .WriteTextAsync(
                $"{fixture.JobId}/diff.txt",
                diff,
                "text/plain; charset=utf-8",
                Arg.Is<PostgresStorageWriteOptions>(options =>
                    options.OrganizationId == "org_123"
                    && options.ExpiresAtUtc == fixture.ExpiresAtUtc
                ),
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_WithTokenJobMismatch_ReturnsUnauthorized()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "different-job",
            fixture.Token,
            StreamFor("diff --git a/a.cs b/a.cs"),
            CancellationToken.None
        );

        await Assert.That(result.Result is UnauthorizedHttpResult).IsTrue();
        await fixture
            .Storage.DidNotReceiveWithAnyArgs()
            .WriteTextAsync(default!, default!, default!, default!, default, default);
    }

    [Test]
    public async Task HandleAsync_WithExpiredToken_ReturnsUnauthorized()
    {
        var fixture = Fixture.Create(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("diff --git a/a.cs b/a.cs"),
            CancellationToken.None
        );

        await Assert.That(result.Result is UnauthorizedHttpResult).IsTrue();
    }

    [Test]
    public async Task HandleAsync_WithOversizeBody_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(maxBytes: 12);

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("diff --git a/a.cs b/a.cs"),
            CancellationToken.None
        );

        await AssertBadRequest(result, "diff_too_large");
    }

    [Test]
    public async Task HandleAsync_WithInvalidUtf8_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            new MemoryStream([0xFF, 0xFE]),
            CancellationToken.None
        );

        await AssertBadRequest(result, "invalid_utf8");
    }

    [Test]
    public async Task HandleAsync_WithBlankBody_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("   \n"),
            CancellationToken.None
        );

        await AssertBadRequest(result, "empty_diff");
    }

    [Test]
    public async Task HandleAsync_WithoutGitDiffHeader_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("not a git diff"),
            CancellationToken.None
        );

        await AssertBadRequest(result, "invalid_diff");
    }

    [Test]
    public async Task HandleAsync_ReuploadingSameJob_WritesSamePathAgain()
    {
        var fixture = Fixture.Create();

        await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("diff --git a/first.cs b/first.cs"),
            CancellationToken.None
        );
        await fixture.Handler.HandleAsync(
            fixture.JobId,
            fixture.Token,
            StreamFor("diff --git a/second.cs b/second.cs"),
            CancellationToken.None
        );

        await fixture
            .Storage.Received(2)
            .WriteTextAsync(
                $"{fixture.JobId}/diff.txt",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<PostgresStorageWriteOptions>(),
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            );
    }

    private static async Task AssertBadRequest(
        Results<
            UnauthorizedHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewMcpDiffUploadResponse>
        > result,
        string expectedCode
    )
    {
        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo(expectedCode);
    }

    private static MemoryStream StreamFor(string value) => new(Encoding.UTF8.GetBytes(value));

    private sealed class Fixture
    {
        private Fixture() { }

        public string JobId { get; } = "018ff6a5f6e57b06b4c1a0f9c13e0f12";

        public DateTimeOffset ExpiresAtUtc { get; private init; }

        public string Token { get; private set; } = string.Empty;

        public IStorageProvider<PostgresStorageWriteOptions> Storage { get; private init; } = null!;

        public UploadMcpCodeReviewDiffHandler Handler { get; private init; } = null!;

        public static Fixture Create(int maxBytes = 500_000, DateTimeOffset? expiresAtUtc = null)
        {
            var settings = new CodeReviewSettings
            {
                ReviewRequestLinkEncryptionKey = "test-key",
                DiffUploadMaxBytes = maxBytes,
            };
            var protector = new CodeReviewDiffUploadTokenProtector(settings);
            var storage = Substitute.For<IStorageProvider<PostgresStorageWriteOptions>>();
            storage
                .WriteTextAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<PostgresStorageWriteOptions>(),
                    Arg.Any<StorageContainer>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call => Task.FromResult($"postgres://{call.ArgAt<string>(0)}"));

            var expiry = expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(10);
            var fixture = new Fixture
            {
                ExpiresAtUtc = expiry,
                Storage = storage,
                Handler = new UploadMcpCodeReviewDiffHandler(settings, protector, storage),
            };
            fixture.Token = protector.Protect(
                CodeReviewDiffUploadTokenProtector.CreatePayload(
                    jobId: fixture.JobId,
                    expiresAtUtc: expiry,
                    createdById: "usr_123",
                    organizationId: "org_123"
                )
            );

            return fixture;
        }
    }
}
