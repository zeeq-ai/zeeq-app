using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Imports a verified library export package into the target library.
/// </summary>
/// <remarks>
/// Import repeats the same verification and planning work as preview rather than trusting a prior
/// browser-side preview response. This keeps the endpoint safe if the library changes between
/// preview and import, or if a caller skips preview entirely. Remote/synced collisions are blocked,
/// while duplicate local paths are allowed only when the caller explicitly confirms overwrite.
/// </remarks>
internal sealed class ImportLibraryHandler(
    ILibraryDocumentStore store,
    LibraryImportPackageReader reader,
    LibraryDocumentWriteService writer
) : IEndpointHandler
{
    /// <summary>
    /// Verifies and imports package documents into the requested library.
    /// </summary>
    /// <remarks>
    /// The signed package is read from the upload on every call. The operation is intentionally
    /// retryable: if a write fails after earlier documents are upserted, the user can upload the same
    /// verified package again and the remaining local paths will be reconciled through upsert.
    /// </remarks>
    public async Task<
        Results<
            Ok<LibraryImportResponse>,
            BadRequest<LibraryError>,
            Conflict<LibraryImportConflictResponse>,
            NotFound
        >
    > HandleAsync(
        string orgId,
        string name,
        IFormFile? file,
        bool overwriteDuplicates,
        ClaimsPrincipal user,
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

        var read = await reader.ReadAsync(file, ct);
        if (!read.IsValid)
        {
            return TypedResults.BadRequest(new LibraryError(read.ErrorMessage!));
        }

        // Recompute the plan at write time so stale previews cannot overwrite newly synced paths.
        var existing = await store.ListDocumentsAsync(orgId, library.Id, ct);
        var plan = LibraryImportPackageReader.CreatePlan(read.Package!, existing);
        if (plan.BlockedRemotePaths.Length > 0)
        {
            // Source-backed documents remain authoritative; importing over them would convert
            // external content into local content and break future sync semantics.
            return TypedResults.Conflict(
                new LibraryImportConflictResponse(
                    [],
                    plan.BlockedRemotePaths,
                    "The import contains paths owned by synced documents."
                )
            );
        }

        if (plan.DuplicateLocalPaths.Length > 0 && !overwriteDuplicates)
        {
            // Local duplicate paths are safe to overwrite, but only after an explicit UI/user
            // confirmation because existing hand-authored content will be replaced.
            return TypedResults.Conflict(
                new LibraryImportConflictResponse(
                    plan.DuplicateLocalPaths,
                    [],
                    "The import would overwrite existing local documents."
                )
            );
        }

        // NOTE: Partial import state is acceptable for this workflow. A retry may require
        // overwrite confirmation for documents written by the previous attempt, but upsert keeps
        // the same verified package reconcilable without corrupting remote/synced paths.
        foreach (var document in plan.Package.Documents)
        {
            await writer.UpsertDocumentAsync(
                orgId,
                user.AsZeeqMinimalIdentity().TeamId,
                library.Id,
                document.Path,
                document.Content,
                ct
            );
        }

        return TypedResults.Ok(
            new LibraryImportResponse(
                CreatedCount: plan.NewPaths.Length,
                UpdatedCount: plan.DuplicateLocalPaths.Length,
                UpdatedPaths: plan.DuplicateLocalPaths
            )
        );
    }
}
