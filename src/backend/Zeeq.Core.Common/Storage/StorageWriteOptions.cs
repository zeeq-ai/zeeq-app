namespace Zeeq.Core.Common.Storage;

/// <summary>
/// Base write options shared by storage providers.
/// </summary>
public abstract record StorageWriteOptions
{
    /// <summary>Optional expiry after which the object can be cleaned up.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>Provider-neutral metadata serialized with the stored object.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
