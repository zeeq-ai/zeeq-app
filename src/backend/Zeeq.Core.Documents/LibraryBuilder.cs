namespace Zeeq.Core.Documents;

/// <summary>
/// Fluent constructor for <see cref="Library"/> that prevents invalid column
/// combinations at compile time.
/// </summary>
/// <remarks>
/// A <see cref="Library"/> has three mutually-exclusive source configurations —
/// local (no source), public-source, and private-source. Each factory method
/// returns a typed variant record that carries only the fields relevant to that
/// variant. The types prevent misconfiguration:
/// <list type="bullet">
///   <item><c>ForLocal().WithIncludeFilter(…)</c> — compile error; no such method</item>
///   <item><c>ForPublicSource().SourceRepoUrl</c> — compile error; no such property</item>
///   <item><c>ForPrivateSource("GitHub")</c> — compile error; <c>sourceRepoUrl</c> is required</item>
/// </list>
/// <c>Build(…)</c> accepts identity fields (<c>id</c>, <c>organizationId</c>,
/// <c>name</c>) and returns a fully-constructed <see cref="Library"/>.
/// <para>
/// Helper methods named <c>BuildFrom</c> copy identity, timestamps, and lifecycle
/// fields from an existing row to simplify update paths.
/// </para>
/// </remarks>
public static class LibraryBuilder
{
    // ── Entry points ───────────────────────────────────────────────────

    /// <summary>Hand-authored documents only; no external source.</summary>
    public static LocalVariant ForLocal() => new();

    /// <summary>Subscribes to a public, globally-ingested repository.</summary>
    /// <param name="publicSourceId">FK into <c>docs_public_sources</c>.</param>
    /// <param name="includeFilters">
    /// Org-level include globs, set directly rather than appended one at a
    /// time — convenient when a caller already has a full array (e.g. a form
    /// submission) rather than building it up via <see cref="PublicSourceVariant.WithIncludeFilter"/>.
    /// </param>
    /// <param name="excludeFilters">Org-level exclude globs, set directly.</param>
    public static PublicSourceVariant ForPublicSource(
        string publicSourceId,
        string[]? includeFilters = null,
        string[]? excludeFilters = null
    ) => new(publicSourceId, includeFilters, excludeFilters);

    /// <summary>Ingests a private, organization-owned repository.</summary>
    /// <param name="sourceKind">Repository provider kind, e.g. "GitHub".</param>
    /// <param name="sourceRepoUrl">Canonical repository URL.</param>
    /// <param name="includeFilters">Org-level include globs, set directly (see <see cref="ForPublicSource"/>).</param>
    /// <param name="excludeFilters">Org-level exclude globs, set directly.</param>
    public static PrivateSourceVariant ForPrivateSource(
        string sourceKind,
        string sourceRepoUrl,
        string[]? includeFilters = null,
        string[]? excludeFilters = null
    ) =>
        new(
            sourceKind,
            sourceRepoUrl,
            IncludeFilters: includeFilters,
            ExcludeFilters: excludeFilters
        );

    // ── Variant carriers ───────────────────────────────────────────────

    /// <summary>No external source — hand-authored documents only.</summary>
    /// <remarks>
    /// The resulting library has <c>PublicSourceId</c>, <c>SourceKind</c>,
    /// <c>SourceRepoUrl</c>, and all filter arrays initialized to their empty
    /// defaults. Sync lifecycle fields are left <c>null</c>.
    /// </remarks>
    public sealed record LocalVariant
    {
        /// <summary>
        /// Builds a local (hand-authored) library.
        /// </summary>
        /// <param name="id">Stable library identifier (use <c>Id.Random()</c>).</param>
        /// <param name="organizationId">Owning organization.</param>
        /// <param name="name">Human-readable, route-safe library name.</param>
        /// <param name="teamId">Optional owning team.</param>
        /// <param name="description">Optional human-readable description.</param>
        public Library Build(
            string id,
            string organizationId,
            string name,
            string? teamId = null,
            string? description = null
        ) =>
            new()
            {
                Id = id,
                OrganizationId = organizationId,
                TeamId = teamId,
                Name = name,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        /// <summary>
        /// Builds a local library carrying identity, timestamps, and lifecycle fields
        /// from the supplied <paramref name="existing"/> row. The <paramref name="name"/>
        /// and <paramref name="description"/> are the only mutable fields.
        /// </summary>
        public Library BuildFrom(
            Library existing,
            string? name = null,
            string? description = null
        ) =>
            new()
            {
                Id = existing.Id,
                OrganizationId = existing.OrganizationId,
                TeamId = existing.TeamId,
                Name = name ?? existing.Name,
                Description = description ?? existing.Description,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
    }

    /// <summary>Subscribes to a public, globally-ingested repository.</summary>
    /// <param name="PublicSourceId">FK into <c>docs_public_sources</c>.</param>
    /// <param name="IncludeFilters">Org-level include globs. Appended via <see cref="WithIncludeFilter"/>.</param>
    /// <param name="ExcludeFilters">Org-level exclude globs. Appended via <see cref="WithExcludeFilter"/>.</param>
    public sealed record PublicSourceVariant(
        string PublicSourceId,
        string[]? IncludeFilters = null,
        string[]? ExcludeFilters = null
    )
    {
        /// <summary>Appends an include glob pattern.</summary>
        public PublicSourceVariant WithIncludeFilter(string filter) =>
            this with
            {
                IncludeFilters = (IncludeFilters ?? []).Append(filter).ToArray(),
            };

        /// <summary>Appends an exclude glob pattern.</summary>
        public PublicSourceVariant WithExcludeFilter(string filter) =>
            this with
            {
                ExcludeFilters = (ExcludeFilters ?? []).Append(filter).ToArray(),
            };

        /// <inheritdoc cref="LocalVariant.Build" />
        public Library Build(
            string? id = null,
            string? organizationId = null,
            string? name = null,
            string? teamId = null,
            string? description = null
        ) =>
            new()
            {
                Id = id ?? throw new ArgumentNullException(nameof(id)),
                OrganizationId =
                    organizationId ?? throw new ArgumentNullException(nameof(organizationId)),
                TeamId = teamId,
                Name = name ?? throw new ArgumentNullException(nameof(name)),
                Description = description,
                PublicSourceId = PublicSourceId,
                IncludeFilters = IncludeFilters ?? [],
                ExcludeFilters = ExcludeFilters ?? [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        /// <summary>
        /// Builds from an existing public-source library row, carrying forward
        /// identity, timestamps, and lifecycle fields. Mutable fields are
        /// overridden by the provided values.
        /// </summary>
        public Library BuildFrom(
            Library existing,
            string? name = null,
            string? description = null
        ) =>
            new()
            {
                Id = existing.Id,
                OrganizationId = existing.OrganizationId,
                TeamId = existing.TeamId,
                Name = name ?? existing.Name,
                Description = description ?? existing.Description,
                PublicSourceId = existing.PublicSourceId,
                IncludeFilters = existing.IncludeFilters,
                ExcludeFilters = existing.ExcludeFilters,
                SourceSyncedAt = existing.SourceSyncedAt,
                SyncStatus = existing.SyncStatus,
                NextSyncAt = existing.NextSyncAt,
                ActiveSyncRunId = existing.ActiveSyncRunId,
                ActiveSyncRunCreatedAtUtc = existing.ActiveSyncRunCreatedAtUtc,
                SyncQueuedAtUtc = existing.SyncQueuedAtUtc,
                SyncStartedAtUtc = existing.SyncStartedAtUtc,
                ManualTriggerHistory = existing.ManualTriggerHistory,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
    }

    /// <summary>Ingests a private, organization-owned repository.</summary>
    /// <param name="SourceKind">Repository provider kind, e.g. "GitHub".</param>
    /// <param name="SourceRepoUrl">Canonical repository URL.</param>
    /// <param name="SourceDefaultIncludeFilters">Source-suggested include globs. Appended via <see cref="WithDefaultIncludeFilter"/>.</param>
    /// <param name="SourceDefaultExcludeFilters">Source-suggested exclude globs. Appended via <see cref="WithDefaultExcludeFilter"/>.</param>
    /// <param name="IncludeFilters">Org-level include globs. Appended via <see cref="WithIncludeFilter"/>.</param>
    /// <param name="ExcludeFilters">Org-level exclude globs. Appended via <see cref="WithExcludeFilter"/>.</param>
    public sealed record PrivateSourceVariant(
        string SourceKind,
        string SourceRepoUrl,
        string[]? SourceDefaultIncludeFilters = null,
        string[]? SourceDefaultExcludeFilters = null,
        string[]? IncludeFilters = null,
        string[]? ExcludeFilters = null
    )
    {
        /// <summary>Appends a source-default include glob pattern.</summary>
        public PrivateSourceVariant WithDefaultIncludeFilter(string filter) =>
            this with
            {
                SourceDefaultIncludeFilters = [.. (SourceDefaultIncludeFilters ?? []), filter],
            };

        /// <summary>Appends a source-default exclude glob pattern.</summary>
        public PrivateSourceVariant WithDefaultExcludeFilter(string filter) =>
            this with
            {
                SourceDefaultExcludeFilters = (SourceDefaultExcludeFilters ?? [])
                    .Append(filter)
                    .ToArray(),
            };

        /// <summary>Appends an org-level include glob pattern.</summary>
        public PrivateSourceVariant WithIncludeFilter(string filter) =>
            this with
            {
                IncludeFilters = [.. (IncludeFilters ?? []), filter],
            };

        /// <summary>Appends an org-level exclude glob pattern.</summary>
        public PrivateSourceVariant WithExcludeFilter(string filter) =>
            this with
            {
                ExcludeFilters = [.. (ExcludeFilters ?? []), filter],
            };

        /// <inheritdoc cref="PublicSourceVariant.Build" />
        public Library Build(
            string? id = null,
            string? organizationId = null,
            string? name = null,
            string? teamId = null,
            string? description = null
        ) =>
            new()
            {
                Id = id ?? throw new ArgumentNullException(nameof(id)),
                OrganizationId =
                    organizationId ?? throw new ArgumentNullException(nameof(organizationId)),
                TeamId = teamId,
                Name = name ?? throw new ArgumentNullException(nameof(name)),
                Description = description,
                SourceKind = SourceKind,
                SourceRepoUrl = SourceRepoUrl,
                SourceDefaultIncludeFilters = SourceDefaultIncludeFilters ?? [],
                SourceDefaultExcludeFilters = SourceDefaultExcludeFilters ?? [],
                IncludeFilters = IncludeFilters ?? [],
                ExcludeFilters = ExcludeFilters ?? [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        /// <summary>
        /// Builds from an existing private-source library row, carrying forward
        /// identity, timestamps, and lifecycle fields.
        /// </summary>
        public Library BuildFrom(
            Library existing,
            string? name = null,
            string? description = null
        ) =>
            new()
            {
                Id = existing.Id,
                OrganizationId = existing.OrganizationId,
                TeamId = existing.TeamId,
                Name = name ?? existing.Name,
                Description = description ?? existing.Description,
                SourceKind = existing.SourceKind,
                SourceRepoUrl = existing.SourceRepoUrl,
                SourceDefaultIncludeFilters = existing.SourceDefaultIncludeFilters,
                SourceDefaultExcludeFilters = existing.SourceDefaultExcludeFilters,
                IncludeFilters = existing.IncludeFilters,
                ExcludeFilters = existing.ExcludeFilters,
                SourceSyncedAt = existing.SourceSyncedAt,
                SyncStatus = existing.SyncStatus,
                NextSyncAt = existing.NextSyncAt,
                ActiveSyncRunId = existing.ActiveSyncRunId,
                ActiveSyncRunCreatedAtUtc = existing.ActiveSyncRunCreatedAtUtc,
                SyncQueuedAtUtc = existing.SyncQueuedAtUtc,
                SyncStartedAtUtc = existing.SyncStartedAtUtc,
                ManualTriggerHistory = existing.ManualTriggerHistory,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
    }
}
