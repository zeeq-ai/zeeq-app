namespace Zeeq.Core.Documents;

/// <summary>
/// Persisted row backing <see cref="IExternalPendingContentSyncStore"/>. A set — not an event log —
/// keyed <c>(organization_id, library_id, external_content_id)</c>, so N edits to one item before the
/// next sync run collapse into one pending row and therefore one content fetch.
/// </summary>
public sealed class ExternalPendingContentSyncRow
{
    /// <summary>Owning organization — leads the key per the distribution-key convention.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Owning library.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Provider-side content id that changed (e.g. a Notion page id).</summary>
    public required string ExternalContentId { get; init; }

    /// <summary>Last webhook event type seen for this item (e.g. <c>page.content_updated</c>).</summary>
    public required string EventType { get; set; }

    /// <summary>Bumped on every upsert.</summary>
    public DateTimeOffset MarkedDirtyAtUtc { get; set; }

    /// <summary>Set when a sync run claims this row. Reset to null by a re-dirtying upsert.</summary>
    public string? ClaimedByRunId { get; set; }

    /// <summary>When this row was claimed.</summary>
    public DateTimeOffset? ClaimedAtUtc { get; set; }
}
