namespace Zeeq.Core.Common.Storage;

/// <summary>
/// Provider-neutral metadata for a stored object.
/// </summary>
public sealed record StorageObjectMetadata(
    string Uri,
    string Path,
    string ContentType,
    long ContentLength,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc
);
