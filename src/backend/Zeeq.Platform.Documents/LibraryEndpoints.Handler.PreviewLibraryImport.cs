using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Verifies a signed library export package and returns the import impact without writing.
/// </summary>
/// <remarks>
/// Preview and import deliberately share <see cref="LibraryImportPackageReader"/> and
/// <see cref="LibraryImportPackageReader.CreatePlan"/> so the UI confirmation describes the same
/// path classification that the write endpoint will enforce. The reader verifies the Zeeq envelope
/// and size limits before parsing the internal zip; this handler only computes target-library impact
/// after provenance has been established.
/// </remarks>
internal sealed class PreviewLibraryImportHandler(
    ILibraryDocumentStore store,
    LibraryImportPackageReader reader
) : IEndpointHandler
{
    /// <summary>
    /// Reads a signed package and classifies its paths as new, duplicate local, or blocked remote.
    /// </summary>
    /// <remarks>
    /// No document writes happen here. Duplicate local paths are returned so the UI can warn that
    /// import will overwrite them, while remote/synced collisions are returned as blocked because
    /// imports must not replace source-owned content.
    /// </remarks>
    public async Task<
        Results<Ok<LibraryImportPreviewResponse>, BadRequest<LibraryError>, NotFound>
    > HandleAsync(string orgId, string name, IFormFile? file, CancellationToken ct)
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

        var read = await reader.ReadAsync(file, ct);
        if (!read.IsValid)
        {
            return TypedResults.BadRequest(new LibraryError(read.ErrorMessage!));
        }

        // Planning runs only after signature verification; untrusted files never reach the
        // target-library collision checks.
        var existing = await store.ListDocumentsAsync(orgId, library.Id, ct);
        var plan = LibraryImportPackageReader.CreatePlan(read.Package!, existing);

        return TypedResults.Ok(
            new LibraryImportPreviewResponse(
                plan.Package.Documents.Length,
                plan.NewPaths,
                plan.DuplicateLocalPaths,
                plan.BlockedRemotePaths
            )
        );
    }
}
