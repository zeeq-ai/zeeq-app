namespace Zeeq.Core.Common.Storage;

/// <summary>
/// Provider-neutral object storage contract.
/// </summary>
/// <remarks>
/// Stream operations are the primary API for large artifacts such as code-review
/// XML and uploaded diffs. Text and byte helpers are convenience wrappers.
/// </remarks>
public interface IStorageProvider<TWriteOptions>
    where TWriteOptions : StorageWriteOptions
{
    /// <summary>Creates a short-lived privileged read URL for an object.</summary>
    Task<string> CreatePrivilegedReadUrlAsync(
        string path,
        TimeSpan validFor,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates a short-lived privileged read URL with response disposition.</summary>
    Task<string> CreatePrivilegedReadUrlAsync(
        string path,
        TimeSpan validFor,
        string responseContentDisposition,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates a short-lived privileged write URL for an object.</summary>
    Task<string> CreatePrivilegedWriteUrlAsync(
        string path,
        TimeSpan validFor,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Creates a short-lived privileged write URL with content type metadata.</summary>
    Task<string> CreatePrivilegedWriteUrlAsync(
        string path,
        TimeSpan validFor,
        string? contentType,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns whether an object exists.</summary>
    Task<bool> ExistsAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns metadata for an object, or <see langword="null"/> when missing.</summary>
    Task<StorageObjectMetadata?> GetMetadataAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes one object if it exists.</summary>
    Task<bool> DeleteAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes objects whose paths start with <paramref name="prefix"/>.</summary>
    Task<int> DeleteByPrefixAsync(
        string prefix,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads an object into memory as bytes.</summary>
    Task<byte[]> ReadBytesAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Copies object contents to a caller-owned stream.</summary>
    Task CopyToAsync(
        string path,
        Stream destination,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Opens an object stream.</summary>
    Task<Stream> ReadAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads an object into memory as UTF-8 text.</summary>
    Task<string> ReadTextAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Writes an in-memory byte array as an object.</summary>
    Task<string> WriteBytesAsync(
        string path,
        byte[] bytes,
        TWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Writes stream contents as an object.</summary>
    Task<string> WriteAsync(
        string path,
        Stream source,
        string? contentType,
        TWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );

    /// <summary>Writes text as an object.</summary>
    Task<string> WriteTextAsync(
        string path,
        string content,
        string contentType,
        TWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    );
}
