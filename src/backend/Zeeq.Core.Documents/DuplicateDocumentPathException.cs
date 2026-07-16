namespace Zeeq.Core.Documents;

/// <summary>
/// Thrown when a document path (live path or previous-path alias) is already occupied
/// within a library, preventing a rename or upsert that would create ambiguity.
/// </summary>
/// <remarks>
/// This is a domain-level exception surfaced via HTTP 409 Conflict by the rename handler.
/// The collision check scans both the live <c>Path</c> and the <c>PreviousPaths</c> array
/// so old aliases cannot be reused by a different document.
/// </remarks>
public sealed class DuplicateDocumentPathException(string path)
    : InvalidOperationException($"Document path already exists: {path}");
