using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Parsing;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Builds and parses the internal zip payload used by signed library export packages.
/// </summary>
internal sealed class LibraryExportPackageService
{
    private const string ManifestEntryName = "manifest.json";
    private const string DocumentEntryPrefix = "documents/";
    private const int MaxManifestBytes = 16 * 1024;
    private const int MaxAggregateDocumentContentBytes =
        LibraryExportPackageProtector.MaxPackageBytes;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public byte[] CreateZipPayload(IReadOnlyCollection<LibraryDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntries = new List<LibraryExportManifestDocument>(documents.Count);

            foreach (
                var document in documents.OrderBy(document => document.Path, StringComparer.Ordinal)
            )
            {
                if (document.SyncRunId is not null || document.SourceOrigin is not null)
                {
                    continue;
                }

                var normalizedPath = ToPackagePath(DocumentNormalizer.NormalizePath(document.Path));
                var contentBytes = Encoding.UTF8.GetBytes(document.Content);
                var hash = ComputeSha256Hex(contentBytes);

                manifestEntries.Add(new(normalizedPath, hash));

                var entry = archive.CreateEntry(
                    DocumentEntryPrefix + normalizedPath,
                    CompressionLevel.SmallestSize
                );
                using var entryStream = entry.Open();
                entryStream.Write(contentBytes);
            }

            WriteManifest(archive, new LibraryExportManifest([.. manifestEntries]));
        }

        return stream.ToArray();
    }

    public LibraryExportPackage ParseZipPayload(byte[] zipPayload)
    {
        ArgumentNullException.ThrowIfNull(zipPayload);

        using var stream = new MemoryStream(zipPayload, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var manifestEntry = GetSingleEntry(archive, ManifestEntryName);
        var manifest = ReadManifest(manifestEntry);
        var normalizedPaths = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<LibraryExportPackageDocument>(manifest.Documents.Length);
        var remainingDocumentBytes = MaxAggregateDocumentContentBytes;

        foreach (var manifestDocument in manifest.Documents)
        {
            string normalizedPath;
            try
            {
                normalizedPath = ToPackagePath(
                    DocumentNormalizer.NormalizePath(manifestDocument.Path)
                );
            }
            catch (ArgumentException ex)
            {
                throw new LibraryExportPackageValidationException(
                    $"The package contains invalid document path '{manifestDocument.Path}'.",
                    ex
                );
            }
            if (!normalizedPaths.Add(normalizedPath))
            {
                throw new LibraryExportPackageValidationException(
                    $"The package contains duplicate document path '{normalizedPath}'."
                );
            }

            if (normalizedPath != manifestDocument.Path)
            {
                throw new LibraryExportPackageValidationException(
                    $"The package contains non-normalized document path '{manifestDocument.Path}'."
                );
            }

            var entryName = DocumentEntryPrefix + normalizedPath;
            var documentEntry = GetSingleEntry(archive, entryName);
            var contentBytes = ReadEntryBytes(
                documentEntry,
                remainingDocumentBytes,
                $"The package document '{normalizedPath}' exceeds the import size limit."
            );
            remainingDocumentBytes -= contentBytes.Length;

            var actualHash = ComputeSha256Hex(contentBytes);
            if (
                !string.Equals(
                    actualHash,
                    manifestDocument.Sha256,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new LibraryExportPackageValidationException(
                    $"The package document '{normalizedPath}' does not match its manifest hash."
                );
            }

            var content = Encoding.UTF8.GetString(contentBytes);
            documents.Add(new(normalizedPath, content));
        }

        return new([.. documents]);
    }

    private static void WriteManifest(ZipArchive archive, LibraryExportManifest manifest)
    {
        var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
    }

    private static LibraryExportManifest ReadManifest(ZipArchiveEntry entry)
    {
        try
        {
            var bytes = ReadEntryBytes(
                entry,
                MaxManifestBytes,
                $"The package manifest exceeds the {MaxManifestBytes} byte limit."
            );

            return JsonSerializer.Deserialize<LibraryExportManifest>(bytes, JsonOptions)
                ?? throw new LibraryExportPackageValidationException(
                    "The package manifest is empty."
                );
        }
        catch (JsonException ex)
        {
            throw new LibraryExportPackageValidationException(
                "The package manifest is not valid JSON.",
                ex
            );
        }
    }

    private static ZipArchiveEntry GetSingleEntry(ZipArchive archive, string fullName)
    {
        var matches = archive
            .Entries.Where(entry =>
                string.Equals(entry.FullName, fullName, StringComparison.Ordinal)
            )
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new LibraryExportPackageValidationException(
                $"The package is missing required entry '{fullName}'."
            ),
            _ => throw new LibraryExportPackageValidationException(
                $"The package contains duplicate entry '{fullName}'."
            ),
        };
    }

    private static byte[] ReadEntryBytes(
        ZipArchiveEntry entry,
        int maxBytes,
        string tooLargeMessage
    )
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream(capacity: Math.Max(0, Math.Min(maxBytes, 4096)));
        var buffer = new byte[8192];

        while (true)
        {
            var bytesRead = stream.Read(buffer);
            if (bytesRead == 0)
            {
                break;
            }

            if (memory.Length + bytesRead > maxBytes)
            {
                throw new LibraryExportPackageValidationException(tooLargeMessage);
            }

            memory.Write(buffer, 0, bytesRead);
        }

        return memory.ToArray();
    }

    internal static string ToPackagePath(string normalizedPath) => normalizedPath.TrimStart('/');

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record LibraryExportPackage(LibraryExportPackageDocument[] Documents);

internal sealed record LibraryExportPackageDocument(string Path, string Content);

internal sealed record LibraryExportManifest(LibraryExportManifestDocument[] Documents);

internal sealed record LibraryExportManifestDocument(string Path, string Sha256);

internal sealed class LibraryExportPackageValidationException(
    string message,
    Exception? inner = null
) : InvalidOperationException(message, inner);
