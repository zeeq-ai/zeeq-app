using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>Queue message that starts a Notion-backed library ingest run.</summary>
/// <remarks>
/// This is intentionally a system message. A single shared lane makes the configured performer
/// count the actual global Notion concurrency cap instead of multiplying it across tenant buckets.
/// Credentials never travel on the queue; the consumer resolves the current encrypted token.
/// </remarks>
[ConfigurePublisher("ingest.notion.sync")]
public sealed class NotionSyncRequested : Event, ISystemMessage
{
    /// <summary>Creates the event with a generated message id.</summary>
    public NotionSyncRequested()
        : base(Id.Random()) { }

    /// <summary>Organization that owns the library.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Optional team that owns the library.</summary>
    public string? TeamId { get; init; }

    /// <summary>Library to synchronize.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Run identifier minted when the shared library lease is acquired.</summary>
    public required string RunId { get; init; }

    /// <summary>Partition timestamp paired with <see cref="RunId"/>.</summary>
    public required DateTimeOffset RunCreatedAtUtc { get; init; }

    /// <summary>Whether this run processes dirty pages or every visible page.</summary>
    public required ExternalSyncScope Scope { get; init; }

    /// <summary>What initiated this run.</summary>
    public required IngestTriggerReason Trigger { get; init; }

    /// <summary>Trace context captured by the publisher.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
