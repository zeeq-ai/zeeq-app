namespace Zeeq.Data.Postgres;

/// <summary>
/// Local object-storage row used by the Postgres storage provider.
/// </summary>
/// <remarks>
/// The table is intentionally generic so code-review findings and future MCP
/// uploaded diffs can share one local artifact provider.
/// </remarks>
internal sealed class PostgresStorageObject
{
    /// <summary>Provider URI, for example <c>postgres://...</c>.</summary>
    public required string Uri { get; init; }

    /// <summary>Owning organization for retention and diagnostics.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>Logical storage container.</summary>
    public required string Container { get; set; }

    /// <summary>Provider-local object path.</summary>
    public required string Path { get; set; }

    /// <summary>Object MIME type.</summary>
    public required string ContentType { get; set; }

    /// <summary>UTF-8 text content when the object was written as text.</summary>
    public string? ContentText { get; set; }

    /// <summary>Binary content when the object was written from bytes or a stream.</summary>
    public byte[]? ContentBytes { get; set; }

    /// <summary>Provider-neutral metadata JSON.</summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>UTC time when the object was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>UTC time when the object was last updated.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Optional expiration time for cleanup.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
