using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Proves the stored snapshot codec and its bounded integrity boundary.</summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewEvaluationCompressionTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private const string OrganizationId = "org_compression_test";
    private const string RepositoryId = "repo_compression_test";
    private PostgresStorageProvider Storage => new(_context);
    private PostgresCodeReviewEvaluationArtifactStore Artifacts => new(Storage);

    [Test]
    public async Task SnapshotRoundTrip_StoresGzipButKeepsCanonicalIdentity()
    {
        var snapshot = Snapshot();
        var canonical = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);
        var artifact = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            snapshot,
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var stored = await _context
            .StorageObjects.AsNoTracking()
            .SingleAsync(row => row.Uri == artifact.StorageUri);

        await Assert
            .That(artifact.StorageUri.EndsWith("snapshot.json.gz", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(stored.ContentBytes![0]).IsEqualTo((byte)0x1f);
        await Assert.That(stored.ContentBytes[1]).IsEqualTo((byte)0x8b);
        await Assert.That(stored.ContentBytes.Length < canonical.Length).IsTrue();
        await Assert.That(artifact.ByteCount).IsEqualTo(canonical.Length);
        await Assert
            .That(artifact.Sha256)
            .IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(canonical)));
        var read = await Artifacts.ReadSnapshotAsync(
            OrganizationId,
            RepositoryId,
            artifact,
            CancellationToken.None
        );
        await Assert
            .That(CodeReviewEvaluationSnapshotSerializer.Serialize(read).SequenceEqual(canonical))
            .IsTrue();
    }

    [Test]
    [Arguments("crc")]
    [Arguments("truncated")]
    [Arguments("raw-json")]
    public async Task InvalidStoredEncoding_IsRejected(string corruption)
    {
        var artifact = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            Snapshot(),
            CancellationToken.None
        );
        var bytes = await Storage.ReadBytesAsync(artifact.StorageUri["postgres://".Length..]);
        if (corruption == "crc")
        {
            bytes[^8] ^= 0xff;
        }
        else if (corruption == "truncated")
        {
            bytes = bytes[..^8];
        }
        else
        {
            bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(Snapshot());
        }
        artifact = await WriteEncodedAsync(artifact, bytes);

        await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(() =>
            Artifacts.ReadSnapshotAsync(
                OrganizationId,
                RepositoryId,
                artifact,
                CancellationToken.None
            )
        );
    }

    [Test]
    public async Task GzipExpansion_IsBoundedBeforeParsingOrHashing()
    {
        var artifact = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            Snapshot(),
            CancellationToken.None
        );
        var oversized = new byte[CodeReviewEvaluationSnapshot.MaxSerializedBytes + 1];
        artifact = await WriteEncodedAsync(artifact, Gzip(oversized));
        var forgedIdentity = artifact with
        {
            ByteCount = oversized.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(oversized)),
        };

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(
            () =>
                Artifacts.ReadSnapshotAsync(
                    OrganizationId,
                    RepositoryId,
                    forgedIdentity,
                    CancellationToken.None
                )
        );
        await Assert.That(exception!.Message.Contains("5 MiB", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CanonicalHashAndLength_AreVerifiedAfterExpansion(bool wrongHash)
    {
        var artifact = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            Snapshot(),
            CancellationToken.None
        );
        var invalid = wrongHash
            ? artifact with
            {
                Sha256 = new string('0', 64),
            }
            : artifact with
            {
                ByteCount = artifact.ByteCount + 1,
            };

        await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(() =>
            Artifacts.ReadSnapshotAsync(
                OrganizationId,
                RepositoryId,
                invalid,
                CancellationToken.None
            )
        );
    }

    [Test]
    public async Task CandidateOutput_RemainsXmlAndForeignScopeIsRejected()
    {
        const string xml = "<findings><finding>Example</finding></findings>";
        var candidate = await Artifacts.WriteCandidateOutputAsync(
            OrganizationId,
            RepositoryId,
            "run_test",
            "case_test",
            xml,
            CancellationToken.None
        );
        var stored = await Storage.ReadBytesAsync(candidate.StorageUri["postgres://".Length..]);
        await Assert.That(Encoding.UTF8.GetString(stored)).IsEqualTo(xml);
        await Assert
            .That(
                await Artifacts.ReadCandidateOutputAsync(
                    OrganizationId,
                    RepositoryId,
                    candidate,
                    CancellationToken.None
                )
            )
            .IsEqualTo(xml);

        var snapshot = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            Snapshot(),
            CancellationToken.None
        );
        await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(() =>
            Artifacts.ReadSnapshotAsync("org_other", RepositoryId, snapshot, CancellationToken.None)
        );
    }

    [Test]
    public async Task AppendedGzipGarbage_IsRejectedEvenWhenLogicalContentAndFooterMatch()
    {
        var artifact = await Artifacts.WriteSnapshotAsync(
            OrganizationId,
            RepositoryId,
            Snapshot(),
            CancellationToken.None
        );
        var stored = await Storage.ReadBytesAsync(artifact.StorageUri["postgres://".Length..]);
        // Appending a copy of ISIZE used to fool a footer-only truncation check while
        // GZipStream still returned the original JSON. Encoded identity catches the mutation.
        var withGarbage = stored
            .Concat(new byte[] { 0x41, 0x42, 0x43, 0x44 })
            .Concat(stored[^4..])
            .ToArray();
        await ReplaceAsync(artifact, withGarbage);

        await Assert.ThrowsAsync<CodeReviewEvaluationArtifactIntegrityException>(() =>
            Artifacts.ReadSnapshotAsync(
                OrganizationId,
                RepositoryId,
                artifact,
                CancellationToken.None
            )
        );
    }

    private async Task<CodeReviewEvaluationArtifact> WriteEncodedAsync(
        CodeReviewEvaluationArtifact logicalArtifact,
        byte[] bytes
    )
    {
        var encodedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path =
            $"reviewer-evaluations/{OrganizationId}/{RepositoryId}/{Guid.CreateVersion7():N}/{encodedHash}/snapshot.json.gz";
        await Storage.WriteBytesAsync(
            path,
            bytes,
            new PostgresStorageWriteOptions { OrganizationId = OrganizationId }
        );
        return logicalArtifact with { StorageUri = $"postgres://{path}" };
    }

    private Task<string> ReplaceAsync(CodeReviewEvaluationArtifact artifact, byte[] bytes) =>
        Storage.WriteBytesAsync(
            artifact.StorageUri["postgres://".Length..],
            bytes,
            new PostgresStorageWriteOptions { OrganizationId = OrganizationId }
        );

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    private static CodeReviewEvaluationSnapshot Snapshot() =>
        new(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            OrganizationId,
            RepositoryId,
            "zeeq-fixture/compression",
            901,
            "Compression case",
            new string('x', 12000),
            "base",
            "head",
            "fixture",
            "https://example.invalid/pull/901",
            false,
            PullRequestState.Open,
            DateTimeOffset.UtcNow,
            1,
            [
                new(
                    "src/App.cs",
                    null,
                    CodeReviewFileMutationState.Modified,
                    "@@ -1 +1 @@\n-before\n+after",
                    false
                ),
            ]
        );
}
