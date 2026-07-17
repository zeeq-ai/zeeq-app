using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

internal sealed class LibraryImportPackageReader(
    LibraryExportPackageProtector protector,
    LibraryExportPackageService packageService
)
{
    public async Task<LibraryImportPackageReadResult> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return LibraryImportPackageReadResult.Invalid("A Zeeq export file is required.");
        }

        if (!file.FileName.EndsWith(".zeeq-export", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryImportPackageReadResult.Invalid("Only .zeeq-export files can be imported.");
        }

        if (file.Length > LibraryExportPackageProtector.MaxPackageBytes)
        {
            return LibraryImportPackageReadResult.Invalid(
                $"Zeeq export files must be {LibraryExportPackageProtector.MaxPackageBytes} bytes or less."
            );
        }

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var memory = new MemoryStream(capacity: (int)file.Length);
            await stream.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }

        if (!protector.TryUnprotect(bytes, out var header, out var zipPayload))
        {
            return LibraryImportPackageReadResult.Invalid(
                "The Zeeq export file could not be verified."
            );
        }

        try
        {
            var package = packageService.ParseZipPayload(zipPayload);
            if (header!.DocumentCount != package.Documents.Length)
            {
                return LibraryImportPackageReadResult.Invalid(
                    "The Zeeq export file manifest does not match its package contents."
                );
            }

            return LibraryImportPackageReadResult.Valid(package);
        }
        catch (InvalidDataException)
        {
            return LibraryImportPackageReadResult.Invalid(
                "The Zeeq export file contains an invalid internal package."
            );
        }
        catch (LibraryExportPackageValidationException ex)
        {
            return LibraryImportPackageReadResult.Invalid(ex.Message);
        }
    }

    public static LibraryImportPlan CreatePlan(
        LibraryExportPackage package,
        IReadOnlyCollection<LibraryDocument> existingDocuments
    )
    {
        var existingByPath = existingDocuments.ToDictionary(
            document => LibraryExportPackageService.ToPackagePath(
                DocumentNormalizer.NormalizePath(document.Path)
            ),
            document => document,
            StringComparer.Ordinal
        );

        var newPaths = new List<string>();
        var duplicateLocalPaths = new List<string>();
        var blockedRemotePaths = new List<string>();

        foreach (var document in package.Documents)
        {
            if (!existingByPath.TryGetValue(document.Path, out var existing))
            {
                newPaths.Add(ToStoredPath(document.Path));
                continue;
            }

            if (existing.SyncRunId is not null || existing.SourceOrigin is not null)
            {
                blockedRemotePaths.Add(ToStoredPath(document.Path));
            }
            else
            {
                duplicateLocalPaths.Add(ToStoredPath(document.Path));
            }
        }

        return new LibraryImportPlan(
            package,
            [.. newPaths],
            [.. duplicateLocalPaths],
            [.. blockedRemotePaths]
        );
    }

    private static string ToStoredPath(string packagePath) => DocumentNormalizer.NormalizePath(packagePath);
}

internal sealed record LibraryImportPackageReadResult(
    bool IsValid,
    string? ErrorMessage,
    LibraryExportPackage? Package
)
{
    public static LibraryImportPackageReadResult Valid(LibraryExportPackage package) =>
        new(true, null, package);

    public static LibraryImportPackageReadResult Invalid(string message) => new(false, message, null);
}

internal sealed record LibraryImportPlan(
    LibraryExportPackage Package,
    string[] NewPaths,
    string[] DuplicateLocalPaths,
    string[] BlockedRemotePaths
);
