using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;
using Paramore.Brighter;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Queue message that starts a private-source (organization-owned library)
/// repository ingest run.
/// </summary>
/// <remarks>
/// Split into a dedicated type from <see cref="PublicRepositorySyncRequested"/>
/// (rather than one message with a conditional topic) because
/// <see cref="ConfigurePublisherAttribute"/> is a compile-time, per-type
/// declaration — Brighter routes by message type, so two lanes need two
/// types. This message is intentionally thin: it identifies *what* to sync
/// (library, repo, trigger), not *how* — the consumer (Phase 2's
/// <c>RepositorySyncRequestedHandler</c>) resolves the GitHub installation id
/// and builds the full <c>RepositoryIngestJob</c> at dispatch time.
/// <para>
/// <b><see cref="RunId"/>/<see cref="RunCreatedAtUtc"/> are minted by the
/// publisher, not the consumer.</b> The manual-trigger endpoint mints them and
/// returns an <c>IngestRunViewToken</c> encoding <see cref="RunCreatedAtUtc"/>
/// to the caller immediately — before any <c>DocsIngestRun</c> row exists. For
/// that token to resolve to a real row once the consumer eventually processes
/// this message (Phase 2), the consumer must build its
/// <c>RepositoryIngestJob</c> with these exact values rather than minting its
/// own; otherwise the id the caller was handed would never match anything.
/// </para>
/// <para>
/// <b><see cref="ISystemMessage"/>, not <see cref="ITenantMessage"/></b> —
/// despite carrying <see cref="OrganizationId"/>/<see cref="TeamId"/>, this
/// deliberately does not route through the tenant-tier bucket router. Private
/// ingest doesn't need priority lanes per organization; a single shared
/// channel with <c>noOfPerformers</c> set directly to
/// <see cref="IngestSettings.MaxConcurrentPrivate"/>'s value gives an exact,
/// enforced concurrency cap. Tenant-bucket routing would instead fan this out
/// across every priority/default/low bucket (20 by default), multiplying the
/// real achievable concurrency by the bucket count — see
/// <see cref="PrivateRepositorySyncRequestedHandler"/>'s
/// <c>ConfigureConsumer</c> remarks for the concrete before/after numbers that
/// motivated this.
/// </para>
/// </remarks>
[ConfigurePublisher("ingest.private.sync")]
public sealed class PrivateRepositorySyncRequested : Event, ISystemMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public PrivateRepositorySyncRequested()
        : base(Id.Random()) { }

    /// <summary>Organization that owns the library being synced.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Optional team that owns the library within the organization.</summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Run identifier the eventual <c>DocsIngestRun</c> record must use — minted
    /// by the publisher (the trigger endpoint or scheduler), not this message.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>Partition key the eventual run record must use, paired with <see cref="RunId"/>.</summary>
    public required DateTimeOffset RunCreatedAtUtc { get; init; }

    /// <summary>Library whose mapped private repository should be synced.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Canonical repository URL being synced.</summary>
    public required string RepoUrl { get; init; }

    /// <summary>What initiated this sync.</summary>
    public required IngestTriggerReason Trigger { get; init; }

    /// <summary>Trace context captured at the triggering request.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}

/// <summary>
/// Queue message that starts a public-source (globally shared) repository
/// ingest run.
/// </summary>
/// <remarks>
/// System-scoped, not tenant-scoped — a public source is not owned by any one
/// organization (spec §2), so this implements <see cref="ISystemMessage"/>
/// rather than <see cref="ITenantMessage"/>. See
/// <see cref="PrivateRepositorySyncRequested"/> for why this is a separate
/// type rather than a conditional topic on one message.
/// </remarks>
[ConfigurePublisher("ingest.public.sync")]
public sealed class PublicRepositorySyncRequested : Event, ISystemMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public PublicRepositorySyncRequested()
        : base(Id.Random()) { }

    /// <summary>
    /// Run identifier the eventual <c>DocsIngestRun</c> record must use — see
    /// the equivalent remark on <see cref="PrivateRepositorySyncRequested.RunId"/>.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>Partition key the eventual run record must use, paired with <see cref="RunId"/>.</summary>
    public required DateTimeOffset RunCreatedAtUtc { get; init; }

    /// <summary>Public source to sync.</summary>
    public required string PublicSourceId { get; init; }

    /// <summary>Canonical repository URL being synced.</summary>
    public required string RepoUrl { get; init; }

    /// <summary>What initiated this sync.</summary>
    public required IngestTriggerReason Trigger { get; init; }

    /// <summary>Trace context captured at the triggering request.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
