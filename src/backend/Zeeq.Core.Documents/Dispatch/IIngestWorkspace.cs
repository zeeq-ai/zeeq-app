namespace Zeeq.Core.Documents.Dispatch;

/// <summary>
/// A disposable working directory for one repository clone.
/// </summary>
/// <remarks>
/// The runner never hardcodes a path; it always writes under
/// <see cref="RootPath"/>. Swapping local temp storage for a mounted volume
/// is a provider swap (see <see cref="IIngestWorkspaceProvider"/>), invisible to
/// the runner.
/// </remarks>
public interface IIngestWorkspace : IAsyncDisposable
{
    /// <summary>Absolute path to the clone root.</summary>
    string RootPath { get; }

    /// <summary>Public or private — mirrors the job's source kind for path-scheme sanity checks.</summary>
    RepositorySourceKind Kind { get; }
}

/// <summary>
/// Allocates <see cref="IIngestWorkspace"/> instances on the runtime's storage
/// medium (local temp disk, mounted ephemeral disk, ...).
/// </summary>
public interface IIngestWorkspaceProvider
{
    /// <summary>Acquires (creating if necessary) the workspace directory for a job.</summary>
    Task<IIngestWorkspace> AcquireAsync(
        RepositoryIngestJob job,
        CancellationToken cancellationToken
    );
}
