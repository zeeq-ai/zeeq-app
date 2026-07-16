namespace Zeeq.Core.Common.Storage;

/// <summary>
/// Write options for the local Postgres storage provider.
/// </summary>
public sealed record PostgresStorageWriteOptions : StorageWriteOptions
{
    /// <summary>Organization that owns the written object.</summary>
    public required string OrganizationId { get; init; }
}
