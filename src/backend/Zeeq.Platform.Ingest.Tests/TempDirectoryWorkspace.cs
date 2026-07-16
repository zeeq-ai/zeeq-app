using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Test-only <see cref="IIngestWorkspace"/> backed by a real temp directory,
/// so runner tests exercise real file reads without a git clone.
/// </summary>
internal sealed class TempDirectoryWorkspace : IIngestWorkspace
{
    public TempDirectoryWorkspace()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"zeeq-ingest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public RepositorySourceKind Kind => RepositorySourceKind.Public;

    public void WriteFile(string relativePath, string content)
    {
        var absolutePath = Path.Combine(RootPath, relativePath.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
