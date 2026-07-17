using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Creates and verifies signed Zeeq library export envelopes.
/// </summary>
/// <remarks>
/// The envelope is deliberately not a zip file. Import verification checks this wrapper before
/// handing the internal zip bytes to <see cref="System.IO.Compression.ZipArchive"/>.
/// </remarks>
internal sealed class LibraryExportPackageProtector(DocumentSettings settings)
{
    public const int MaxPackageBytes = 500 * 1024;

    private const string Purpose = "Zeeq.Documents.LibraryExport.v1";
    private const string ExpectedKind = "library-local-documents";
    private const int HeaderLengthSize = sizeof(int);
    private const int TagSize = 16;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZEEQEXP1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Wraps a validated internal zip package in a signed Zeeq export envelope.
    /// </summary>
    public byte[] Protect(byte[] zipPayload, DateTimeOffset createdAtUtc, int documentCount)
    {
        ArgumentNullException.ThrowIfNull(zipPayload);

        var header = new LibraryExportPackageHeader(ExpectedKind, createdAtUtc, documentCount);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var signedLength = Magic.Length + HeaderLengthSize + headerBytes.Length + zipPayload.Length;
        var envelopeLength = signedLength + TagSize;
        if (envelopeLength > MaxPackageBytes)
        {
            throw new LibraryExportPackageTooLargeException(envelopeLength, MaxPackageBytes);
        }

        var envelope = new byte[envelopeLength];

        Magic.CopyTo(envelope, 0);
        BinaryPrimitives.WriteInt32BigEndian(
            envelope.AsSpan(Magic.Length, HeaderLengthSize),
            headerBytes.Length
        );
        headerBytes.CopyTo(envelope.AsSpan(Magic.Length + HeaderLengthSize));
        zipPayload.CopyTo(envelope.AsSpan(Magic.Length + HeaderLengthSize + headerBytes.Length));

        var tag = ComputeTag(envelope.AsSpan(0, signedLength));
        tag.CopyTo(envelope.AsSpan(signedLength, TagSize));

        return envelope;
    }

    /// <summary>
    /// Attempts to verify and unwrap a Zeeq export envelope.
    /// </summary>
    public bool TryUnprotect(
        ReadOnlySpan<byte> envelope,
        out LibraryExportPackageHeader? header,
        out byte[] zipPayload
    )
    {
        header = null;
        zipPayload = [];

        if (envelope.Length > MaxPackageBytes)
        {
            return false;
        }

        var minimumLength = Magic.Length + HeaderLengthSize + TagSize;
        if (envelope.Length < minimumLength)
        {
            return false;
        }

        if (!envelope[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(
            envelope.Slice(Magic.Length, HeaderLengthSize)
        );
        if (headerLength <= 0)
        {
            return false;
        }

        var maxHeaderLength = envelope.Length - Magic.Length - HeaderLengthSize - TagSize;
        if (headerLength > maxHeaderLength)
        {
            return false;
        }

        var payloadStart = Magic.Length + HeaderLengthSize + headerLength;
        var payloadLength = envelope.Length - payloadStart - TagSize;
        if (payloadLength < 0)
        {
            return false;
        }

        var signedBytes = envelope[..^TagSize];
        var actualTag = envelope[^TagSize..];
        var expectedTag = ComputeTag(signedBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, actualTag))
        {
            return false;
        }

        try
        {
            header = JsonSerializer.Deserialize<LibraryExportPackageHeader>(
                envelope.Slice(Magic.Length + HeaderLengthSize, headerLength),
                JsonOptions
            );
        }
        catch (JsonException)
        {
            return false;
        }

        if (header is not { Kind: ExpectedKind, DocumentCount: >= 0 })
        {
            header = null;
            return false;
        }

        zipPayload = envelope.Slice(payloadStart, payloadLength).ToArray();
        return true;
    }

    private byte[] ComputeTag(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_key.Value, bytes, hash);
        return hash[..TagSize].ToArray();
    }

    private readonly Lazy<byte[]> _key = new(() =>
    {
        if (string.IsNullOrWhiteSpace(settings.LibraryExportSigningKey))
        {
            throw new InvalidOperationException(
                "AppSettings:Documents:LibraryExportSigningKey is required to protect library export packages."
            );
        }

        return SHA256.HashData(
            Encoding.UTF8.GetBytes(Purpose + "\n" + settings.LibraryExportSigningKey)
        );
    });
}

internal sealed record LibraryExportPackageHeader(
    string Kind,
    DateTimeOffset CreatedAtUtc,
    int DocumentCount
);

internal sealed class LibraryExportPackageTooLargeException(int actualBytes, int maxBytes)
    : InvalidOperationException(
        $"The signed Zeeq export package is {actualBytes} bytes, which exceeds the {maxBytes} byte limit."
    )
{
    public int ActualBytes { get; } = actualBytes;

    public int MaxBytes { get; } = maxBytes;
}
