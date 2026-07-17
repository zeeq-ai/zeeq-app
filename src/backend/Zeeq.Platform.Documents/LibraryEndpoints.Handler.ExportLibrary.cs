using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Exports hand-authored local documents from a library.
/// </summary>
/// <remarks>
/// The handler intentionally has two export modes that start from the same internal zip payload:
/// <c>format=zip</c> returns that payload directly for human-controlled archiving, while the
/// default <c>format=zeeq</c> wraps the payload in a signed Zeeq envelope that the import endpoint
/// can verify before opening the zip. Only local documents are exported because synced documents
/// can be recovered from their source system and should not be imported as user-authored content.
/// </remarks>
internal sealed class ExportLibraryHandler(
    ILibraryDocumentStore store,
    LibraryExportPackageService packageService,
    LibraryExportPackageProtector protector
) : IEndpointHandler
{
    /// <summary>
    /// Creates either a plain archive zip or a signed Zeeq export for the requested library.
    /// </summary>
    /// <remarks>
    /// The signed path performs the size check after the internal zip is built because the final
    /// envelope includes header and HMAC bytes. Oversized signed exports return HTTP 413 instead
    /// of silently falling back to an unsigned zip, keeping import provenance explicit.
    /// </remarks>
    public async Task<IResult> HandleAsync(
        string orgId,
        string name,
        string? format,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var library = await store.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        // Default to the importable Zeeq package; plain zip is an explicit archive-only opt-in.
        var exportFormat = string.IsNullOrWhiteSpace(format) ? "zeeq" : format.Trim();
        if (
            !exportFormat.Equals("zeeq", StringComparison.OrdinalIgnoreCase)
            && !exportFormat.Equals("zip", StringComparison.OrdinalIgnoreCase)
        )
        {
            return TypedResults.BadRequest(new LibraryError("Export format must be zeeq or zip."));
        }

        var documents = await store.ListDocumentsAsync(orgId, library.Id, ct);

        // Source-backed documents are deliberately excluded so an import never converts synced
        // GitHub content into local authored content in the target organization.
        var localDocuments = documents
            .Where(document => document.SyncRunId is null && document.SourceOrigin is null)
            .ToArray();
        if (localDocuments.Length == 0)
        {
            return TypedResults.BadRequest(
                new LibraryError("This library does not have any local documents to export.")
            );
        }

        var zipPayload = packageService.CreateZipPayload(localDocuments);
        var date = DateOnly.FromDateTime(DateTime.UtcNow);

        // The raw zip uses the same manifest/content layout as the signed package, but the import
        // endpoint rejects it because it lacks the Zeeq envelope signature.
        if (exportFormat.Equals("zip", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.File(
                zipPayload,
                "application/zip",
                LibraryExportFileNames.Create(library.Name, date, "zip")
            );
        }

        try
        {
            // Protect signs the envelope over both metadata and zip bytes; import verification
            // happens before ZipArchive sees the payload.
            var package = protector.Protect(
                zipPayload,
                DateTimeOffset.UtcNow,
                localDocuments.Length
            );
            return TypedResults.File(
                package,
                "application/octet-stream",
                LibraryExportFileNames.Create(library.Name, date, "zeeq-export")
            );
        }
        catch (LibraryExportPackageTooLargeException ex)
        {
            return TypedResults.Problem(
                ex.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge
            );
        }
    }
}
