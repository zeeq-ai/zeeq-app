using System.Text;
using System.Text.Json;
using Zeeq.Core.Common.Storage;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres;

/// <summary>
/// Postgres-backed local object storage provider.
/// </summary>
/// <remarks>
/// This provider is intended for local/development artifacts and small durable
/// workflow payloads. The stream-first contract keeps callers from depending on
/// in-memory text payloads when the storage backend later moves to GCS.
/// </remarks>
internal sealed class PostgresStorageProvider(PostgresDbContext db)
    : IStorageProvider<PostgresStorageWriteOptions>
{
    /// <inheritdoc />
    public Task<string> CreatePrivilegedReadUrlAsync(
        string path,
        TimeSpan validFor,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ToUri(path, container));

    /// <inheritdoc />
    public Task<string> CreatePrivilegedReadUrlAsync(
        string path,
        TimeSpan validFor,
        string responseContentDisposition,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ToUri(path, container));

    /// <inheritdoc />
    public Task<string> CreatePrivilegedWriteUrlAsync(
        string path,
        TimeSpan validFor,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ToUri(path, container));

    /// <inheritdoc />
    public Task<string> CreatePrivilegedWriteUrlAsync(
        string path,
        TimeSpan validFor,
        string? contentType,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ToUri(path, container));

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    ) =>
        await db
            .StorageObjects.TagWithOperationCallSite("storage_object.exists")
            .AnyAsync(
                storageObject =>
                    storageObject.Container == container.ToString()
                    && storageObject.Path == NormalizePath(path),
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<StorageObjectMetadata?> GetMetadataAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        var storageObject = await FindAsync(path, container, cancellationToken);

        return storageObject is null ? null : ToMetadata(storageObject);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        var storageObject = await FindAsync(path, container, cancellationToken);
        if (storageObject is null)
        {
            return false;
        }

        db.StorageObjects.Remove(storageObject);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByPrefixAsync(
        string prefix,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedPrefix = NormalizePath(prefix);
        var rows = await db
            .StorageObjects.TagWithOperationCallSite("storage_object.delete_by_prefix")
            .Where(storageObject =>
                storageObject.Container == container.ToString()
                && storageObject.Path.StartsWith(normalizedPrefix)
            )
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0)
        {
            return 0;
        }

        db.StorageObjects.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);

        return rows.Length;
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadBytesAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = await ReadAsync(path, container, cancellationToken);
        using var memory = new MemoryStream();

        await stream.CopyToAsync(memory, cancellationToken);

        return memory.ToArray();
    }

    /// <inheritdoc />
    public async Task CopyToAsync(
        string path,
        Stream destination,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        await using var source = await ReadAsync(path, container, cancellationToken);

        await source.CopyToAsync(destination, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream> ReadAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        var storageObject =
            await FindAsync(path, container, cancellationToken)
            ?? throw new FileNotFoundException("Stored object was not found.", path);

        var bytes =
            storageObject.ContentBytes
            ?? Encoding.UTF8.GetBytes(storageObject.ContentText ?? string.Empty);

        return new MemoryStream(bytes, writable: false);
    }

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(
        string path,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = await ReadAsync(path, container, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> WriteBytesAsync(
        string path,
        byte[] bytes,
        PostgresStorageWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        await using var stream = new MemoryStream(bytes);

        return await WriteAsync(
            path,
            stream,
            contentType: "application/octet-stream",
            options,
            container,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        string path,
        Stream source,
        string? contentType,
        PostgresStorageWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);

        return await UpsertAsync(
            path,
            memory.ToArray(),
            contentType ?? "application/octet-stream",
            options,
            container,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<string> WriteTextAsync(
        string path,
        string content,
        string contentType,
        PostgresStorageWriteOptions options,
        StorageContainer container = StorageContainer.Default,
        CancellationToken cancellationToken = default
    )
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes);

        return await WriteAsync(path, stream, contentType, options, container, cancellationToken);
    }

    private async Task<string> UpsertAsync(
        string path,
        byte[] bytes,
        string contentType,
        PostgresStorageWriteOptions options,
        StorageContainer container,
        CancellationToken cancellationToken
    )
    {
        var normalizedPath = NormalizePath(path);
        var containerName = container.ToString();
        var existing = await FindAsync(normalizedPath, container, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var uri = ToUri(normalizedPath, container);
        var metadataJson = JsonSerializer.Serialize(options.Metadata);

        if (existing is null)
        {
            db.StorageObjects.Add(
                new PostgresStorageObject
                {
                    Uri = uri,
                    OrganizationId = options.OrganizationId,
                    Container = containerName,
                    Path = normalizedPath,
                    ContentType = contentType,
                    ContentBytes = bytes,
                    MetadataJson = metadataJson,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ExpiresAtUtc = options.ExpiresAtUtc,
                }
            );
        }
        else
        {
            existing.OrganizationId = options.OrganizationId;
            existing.ContentType = contentType;
            existing.ContentBytes = bytes;
            existing.ContentText = null;
            existing.MetadataJson = metadataJson;
            existing.UpdatedAtUtc = now;
            existing.ExpiresAtUtc = options.ExpiresAtUtc;
        }

        await db.SaveChangesAsync(cancellationToken);

        return uri;
    }

    private Task<PostgresStorageObject?> FindAsync(
        string path,
        StorageContainer container,
        CancellationToken cancellationToken
    )
    {
        var normalizedPath = NormalizePath(path);
        var containerName = container.ToString();

        return db
            .StorageObjects.TagWithOperationCallSite("storage_object.find")
            .FirstOrDefaultAsync(
                storageObject =>
                    storageObject.Container == containerName
                    && storageObject.Path == normalizedPath,
                cancellationToken
            );
    }

    private static StorageObjectMetadata ToMetadata(PostgresStorageObject storageObject)
    {
        var contentLength =
            storageObject.ContentBytes?.LongLength
            ?? Encoding.UTF8.GetByteCount(storageObject.ContentText ?? string.Empty);

        return new(
            storageObject.Uri,
            storageObject.Path,
            storageObject.ContentType,
            contentLength,
            storageObject.CreatedAtUtc,
            storageObject.UpdatedAtUtc,
            storageObject.ExpiresAtUtc
        );
    }

    private static string ToUri(string path, StorageContainer container) =>
        $"postgres://{NormalizePath(path)}";

    private static string NormalizePath(string path) => path.Trim().TrimStart('/');
}
