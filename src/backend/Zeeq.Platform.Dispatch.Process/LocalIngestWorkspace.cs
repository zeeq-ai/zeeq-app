using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;

namespace Zeeq.Platform.Dispatch.Process;

/// <summary>
/// A workspace backed by a directory on local disk.
/// </summary>
/// <remarks>
/// Per spec §5.1, local temp is delete-on-dispose — each run starts clean.
/// (This differs from a future <c>MountedVolumeWorkspaceProvider</c>, which
/// per spec leaves the directory intact for reuse across runs on a shared
/// mount.) <see cref="LocalTempWorkspaceProvider"/> still attempts a
/// clone-or-pull reuse check on <c>AcquireAsync</c> for crash resilience — a
/// prior run that died before calling <see cref="DisposeAsync"/> can leave a
/// stale directory behind, and pulling it forward is cheaper than always
/// assuming corruption.
/// </remarks>
internal sealed class LocalIngestWorkspace(string rootPath, RepositorySourceKind kind)
    : IIngestWorkspace
{
    /// <inheritdoc />
    public string RootPath { get; } = rootPath;

    /// <inheritdoc />
    public RepositorySourceKind Kind { get; } = kind;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
