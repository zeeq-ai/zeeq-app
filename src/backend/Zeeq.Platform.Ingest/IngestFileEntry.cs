namespace Zeeq.Platform.Ingest;

/// <summary>
/// One in-scope file discovered under an ingest workspace.
/// </summary>
/// <param name="RelativePath">Path relative to the repository root, forward-slash separated.</param>
/// <param name="AbsolutePath">Absolute path on disk, used to open the file.</param>
public sealed record IngestFileEntry(string RelativePath, string AbsolutePath);
