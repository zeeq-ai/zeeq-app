namespace Zeeq.Core.Documents;

/// <summary>
/// Active run identity cleared while resetting a stuck repository sync.
/// </summary>
public sealed record StalledSyncReset(
    RepositorySourceKind SourceKind,
    string SourceId,
    string? OrganizationId,
    string? LibraryId,
    string? RunId,
    DateTimeOffset? RunCreatedAtUtc
);

/// <summary>
/// Result of manually resetting a private-source library sync.
/// </summary>
public sealed record LibrarySyncStateReset(Library Library, StalledSyncReset ClearedSync);
