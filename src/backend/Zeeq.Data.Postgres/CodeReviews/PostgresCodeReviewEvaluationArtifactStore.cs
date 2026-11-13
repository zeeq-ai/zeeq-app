using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common.Storage;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed immutable artifact store for evaluation snapshots and candidate output.
/// </summary>
/// <remarks>
/// Paths are server-generated and globally distinct. A write is rejected when the path
/// already exists, so a published artifact can never be overwritten. Reads verify the
/// recorded SHA-256 and byte count, which means a corrupted or substituted object is a hard
/// failure instead of a silent recapture. No expiry is set on referenced artifacts.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationArtifactStore(
    IStorageProvider<PostgresStorageWriteOptions> storage
) : ICodeReviewEvaluationArtifactStore
{
    private const string RootPrefix = "reviewer-evaluations";

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationArtifact> WriteSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);
        if (bytes.Length > CodeReviewEvaluationSnapshot.MaxSerializedBytes)
        {
            throw new InvalidOperationException(
                "The captured snapshot exceeds the 5 MiB evaluation limit."
            );
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        var storedBytes = compressed.ToArray();
        Activity.Current?.AddEvent(
            new ActivityEvent(
                "evaluation.snapshot.compressed",
                tags: new ActivityTagsCollection
                {
                    { "snapshot.original_bytes", bytes.Length },
                    { "snapshot.stored_bytes", storedBytes.Length },
                }
            )
        );

        // The logical identity stays stable across codecs. Bind the encoded bytes separately
        // in the server-owned path so even trailing gzip garbage is detected on read.
        var encodedHash = Convert.ToHexStringLower(SHA256.HashData(storedBytes));
        var path = BuildPath(organizationId, repositoryId, $"{encodedHash}/snapshot.json.gz");
        await WriteImmutableAsync(organizationId, path, storedBytes, cancellationToken);

        return new(
            ToStorageUri(path),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationArtifact> WriteCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        string runId,
        string caseId,
        string findingsXml,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        var bytes = Encoding.UTF8.GetBytes(findingsXml);
        var path = BuildPath(
            organizationId,
            repositoryId,
            $"runs/{Sanitize(runId)}/{Sanitize(caseId)}-output.xml"
        );
        await WriteImmutableAsync(organizationId, path, bytes, cancellationToken);

        return new(
            ToStorageUri(path),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationSnapshot> ReadSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadVerifiedAsync(
            organizationId,
            repositoryId,
            artifact,
            cancellationToken,
            decompressSnapshot: true
        );

        return CodeReviewEvaluationSnapshotSerializer.Deserialize(Encoding.UTF8.GetString(bytes));
    }

    /// <inheritdoc />
    public async Task<string> ReadCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadVerifiedAsync(
            organizationId,
            repositoryId,
            artifact,
            cancellationToken
        );

        return Encoding.UTF8.GetString(bytes);
    }

    private static string BuildPath(
        string organizationId,
        string repositoryId,
        string relativeSuffix
    ) =>
        $"{RootPrefix}/{Sanitize(organizationId)}/{Sanitize(repositoryId)}/{Guid.CreateVersion7():N}/{relativeSuffix}";

    private static string Sanitize(string value) =>
        string.Concat(
            value.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'
            )
        );

    private static string ToStorageUri(string path) => $"postgres://{path}";

    private static string ToPath(string storageUri) =>
        storageUri.StartsWith("postgres://", StringComparison.Ordinal)
            ? storageUri["postgres://".Length..]
            : storageUri;

    private async Task WriteImmutableAsync(
        string organizationId,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken
    )
    {
        if (await storage.ExistsAsync(path, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                "Evaluation artifacts are immutable and the target path already exists."
            );
        }

        await storage.WriteBytesAsync(
            path,
            bytes,
            new PostgresStorageWriteOptions { OrganizationId = organizationId },
            cancellationToken: cancellationToken
        );
    }

    private async Task<byte[]> ReadVerifiedAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken,
        bool decompressSnapshot = false
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var path = ToPath(artifact.StorageUri);

        // Parent-scope guard: a caller can never point the reader at another organization's or
        // repository's artifact tree, even if a URI leaked.
        var expectedSegment = $"{RootPrefix}/{Sanitize(organizationId)}/{Sanitize(repositoryId)}/";
        if (!path.StartsWith(expectedSegment, StringComparison.Ordinal))
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact is not scoped to the authorized organization and repository."
            );
        }

        var bytes = await storage.ReadBytesAsync(path, cancellationToken: cancellationToken);
        if (decompressSnapshot)
        {
            var encodedHash = path.Split('/')[^2];
            if (
                !string.Equals(
                    encodedHash,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    StringComparison.Ordinal
                )
            )
            {
                throw new CodeReviewEvaluationArtifactIntegrityException(
                    "The encoded evaluation snapshot hash does not match its storage identity."
                );
            }

            bytes = await DecompressSnapshotAsync(bytes, artifact.ByteCount, cancellationToken);
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact hash does not match its recorded identity."
            );
        }

        if (bytes.Length != artifact.ByteCount)
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact length does not match its recorded identity."
            );
        }

        return bytes;
    }

    /// <summary>
    /// Expands the single supported snapshot format with a strict canonical-size limit.
    /// </summary>
    /// <remarks>
    /// Integrity metadata describes the original JSON, not the gzip stream. Keep the
    /// expansion bounded before hashing so a corrupt artifact cannot allocate arbitrary memory.
    /// </remarks>
    private static async Task<byte[]> DecompressSnapshotAsync(
        byte[] storedBytes,
        int expectedLength,
        CancellationToken cancellationToken
    )
    {
        if (expectedLength is <= 0 or > CodeReviewEvaluationSnapshot.MaxSerializedBytes)
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The expanded evaluation snapshot exceeds the 5 MiB limit or has an invalid length."
            );
        }

        // Gzip adds a small header/trailer and deflate framing even for incompressible input.
        const int maxStoredBytes = CodeReviewEvaluationSnapshot.MaxSerializedBytes + 64 * 1024;
        if (
            storedBytes.Length is < 18 or > maxStoredBytes
            || storedBytes[0] != 0x1f
            || storedBytes[1] != 0x8b
        )
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation snapshot is not a valid gzip artifact."
            );
        }

        try
        {
            using var source = new MemoryStream(storedBytes, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            // The verified logical length bounds a single owned output array. Do not retain
            // a growable stream plus a second full-sized ToArray copy for each read.
            var expanded = new byte[expectedLength];
            await gzip.ReadExactlyAsync(expanded, cancellationToken);
            if (await gzip.ReadAsync(new byte[1], cancellationToken) != 0)
            {
                throw new CodeReviewEvaluationArtifactIntegrityException(
                    "The expanded evaluation snapshot exceeds its recorded byte count."
                );
            }

            // GZipStream can reach EOF without rejecting a missing trailer. Our snapshots
            // use one gzip member; its required ISIZE footer must match the expanded bytes.
            var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(storedBytes.AsSpan(^4));
            if (declaredSize != expanded.Length)
            {
                throw new CodeReviewEvaluationArtifactIntegrityException(
                    "The evaluation snapshot gzip artifact is truncated or has an invalid length."
                );
            }

            return expanded;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation snapshot gzip artifact is corrupt."
            );
        }
    }
}
