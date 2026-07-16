using Zeeq.Core.Documents;

namespace Zeeq.Testing.EntityGraphs;

// ═══════════════════════════════════════════════════════════════════════
//  Prototypes
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Test-builder prototype for <see cref="DocsPublicSource"/> rows.
/// Every field has a sensible default — tests only override what varies.
/// </summary>
public sealed class DocsPublicSourcePrototype
{
    /// <summary>Stable source identifier. Auto-generated when omitted.</summary>
    public string? Id { get; set; }

    /// <summary>Canonical repository URL. Auto-generated when omitted.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Default include globs for subscribing libraries.</summary>
    public string[] DefaultIncludeFilters { get; set; } = [];

    /// <summary>Default exclude globs for subscribing libraries.</summary>
    public string[] DefaultExcludeFilters { get; set; } = [];
}

/// <summary>
/// Test-builder prototype for <see cref="DocsPublicDocument"/> rows.
/// The only required deltas from a test are typically path, content hash,
/// and sync run ID — everything else has a sensible default.
/// </summary>
public sealed class DocsPublicDocumentPrototype
{
    /// <summary>Normalized path within the source. Auto-generated when omitted.</summary>
    public string? Path { get; set; }

    /// <summary>Full markdown content. Defaults to a simple one-heading body.</summary>
    public string Content { get; set; } = "# Test\n\nBody.";

    /// <summary>SHA-256 content hash. Auto-generated when omitted.</summary>
    public string? ContentHash { get; set; }

    /// <summary>The sync run ID that stamps this document. Null means not stamped.</summary>
    public string? SyncRunId { get; set; }

    /// <summary>Display title. Resolved from the first heading when omitted.</summary>
    public string? Title { get; set; }

    /// <summary>Prior paths for move detection. Empty by default.</summary>
    public string[] PreviousPaths { get; set; } = [];

    /// <summary>Whether the generated row should be persisted when the graph is built.</summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for <see cref="DocsIngestRun"/> rows.
/// Defaults to a private-source succeeded run — tests override status
/// and counts to exercise the state machine.
/// </summary>
public sealed class DocsIngestRunPrototype
{
    /// <summary>Run identifier. Auto-generated when omitted.</summary>
    public string? Id { get; set; }

    /// <summary>Partition key. Defaults to <c>DateTimeOffset.UtcNow</c>.</summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>Repository visibility kind.</summary>
    public RepositorySourceKind SourceKind { get; set; } = RepositorySourceKind.Private;

    /// <summary>Repository URL.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Owning organization. Defaults to the seed organization.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Owning library. Auto-generated when omitted.</summary>
    public string? LibraryId { get; set; }

    /// <summary>Public source reference. Resolved from the graph when omitted
    /// and <see cref="SourceKind"/> is <see cref="RepositorySourceKind.Public"/>.</summary>
    public string? PublicSourceId { get; set; }

    /// <summary>What triggered the run.</summary>
    public IngestTriggerReason Trigger { get; set; } = IngestTriggerReason.Manual;

    /// <summary>Current run status.</summary>
    public IngestRunStatus Status { get; set; } = IngestRunStatus.Running;

    /// <summary>File count totals. Only override what varies.</summary>
    public int FilesTotal { get; set; }

    /// <summary>Files newly added.</summary>
    public int FilesAdded { get; set; }

    /// <summary>Files with changed content.</summary>
    public int FilesUpdated { get; set; }

    /// <summary>Files detected at a new path.</summary>
    public int FilesMoved { get; set; }

    /// <summary>Files unchanged.</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Files removed by sweep.</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Files that failed to process.</summary>
    public int FilesFailed { get; set; }

    /// <summary>Whether this run had an auth failure.</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Failure message for partial/failed runs.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>When the run started. Defaults to <c>CreatedAtUtc</c>.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>When the run completed. Null means still running.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Whether the generated row should be persisted when the graph is built.</summary>
    public bool PersistOnBuild { get; set; } = true;
}

// ═══════════════════════════════════════════════════════════════════════
//  Extension methods
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Entity-graph extensions for ingest test seed data.
/// </summary>
public static class EntityGraphDocsIngestExtensions
{
    extension<TState>(EntityGraphBuilder<TState> builder)
    {
        /// <summary>
        /// Adds one public source with sensible defaults.
        /// </summary>
        public EntityGraphBuilder<(TState Previous, DocsPublicSource Source)> AddDocsPublicSource(
            Action<DocsPublicSourcePrototype>? customize = null
        )
        {
            var prototype = new DocsPublicSourcePrototype();
            customize?.Invoke(prototype);

            return builder.Add(seed => CreateDocsPublicSource(seed, prototype));
        }

        /// <summary>
        /// Adds documents to the latest public source in the graph.
        /// Each setup action produces one document; defaults produce one generic document.
        /// </summary>
        public EntityGraphBuilder<(
            TState Previous,
            DocsPublicDocument[] Documents
        )> AddDocsPublicDocuments(params Action<DocsPublicDocumentPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var source = FindLatest<DocsPublicSource>(builder.Entities);
            var rows = new DocsPublicDocument[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new DocsPublicDocumentPrototype();
                customize[index].Invoke(prototype);

                var row = CreateDocsPublicDocument(builder.Seed, source, prototype);
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }

        /// <summary>
        /// Adds ingest run records. Defaults to a single private-source
        /// running run with the seed organization.
        /// </summary>
        public EntityGraphBuilder<(TState Previous, DocsIngestRun[] Runs)> AddDocsIngestRuns(
            params Action<DocsIngestRunPrototype>[] customize
        )
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            // Resolve the latest public source in the graph so public-source
            // runs reference the correct row. If no source is present, the
            // prototype's explicit PublicSourceId is used as-is.
            var publicSource = TryFindLatest<DocsPublicSource>(builder.Entities);
            var rows = new DocsIngestRun[customize.Length];
            var nonPersistentRows = new List<object>();

            for (var index = 0; index < rows.Length; index++)
            {
                var prototype = new DocsIngestRunPrototype();
                customize[index].Invoke(prototype);

                var row = CreateDocsIngestRun(builder.Seed, prototype, publicSource);
                rows[index] = row;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentRows.Add(row);
                }
            }

            return builder.Push(rows, nonPersistentRows);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Factory methods
    // ═══════════════════════════════════════════════════════════════════

    private static DocsPublicSource CreateDocsPublicSource(
        SeedContext seed,
        DocsPublicSourcePrototype prototype
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = prototype.Id ?? SeedContext.NewId("src"),
            Kind = RepositorySourceKind.Public,
            RepoUrl =
                prototype.RepoUrl
                ?? $"https://github.com/example/repo-{SeedContext.NewId("repo")[5..]}",
            Name = "Test Source",
            DefaultIncludeFilters = prototype.DefaultIncludeFilters,
            DefaultExcludeFilters = prototype.DefaultExcludeFilters,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static DocsPublicDocument CreateDocsPublicDocument(
        SeedContext seed,
        DocsPublicSource source,
        DocsPublicDocumentPrototype prototype
    )
    {
        var now = DateTimeOffset.UtcNow;
        var path = prototype.Path ?? $"/guides/{SeedContext.NewId("doc")[5..]}.md";
        var title = prototype.Title ?? "Test Document";

        return new()
        {
            Id = SeedContext.NewId("doc"),
            PublicSourceId = source.Id,
            Path = path,
            Title = title,
            TitleNormalized = title.ToLowerInvariant(),
            Content = prototype.Content,
            ContentHash = prototype.ContentHash ?? SeedContext.NewId("hash"),
            SyncRunId = prototype.SyncRunId,
            PreviousPaths = prototype.PreviousPaths,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static DocsIngestRun CreateDocsIngestRun(
        SeedContext seed,
        DocsIngestRunPrototype prototype,
        DocsPublicSource? publicSource = null
    )
    {
        var createdAt = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;

        return new()
        {
            Id = prototype.Id ?? SeedContext.NewId("run"),
            CreatedAtUtc = createdAt,
            SourceKind = prototype.SourceKind,
            RepoUrl = prototype.RepoUrl ?? "https://github.com/acme/repo",
            PublicSourceId =
                prototype.SourceKind == RepositorySourceKind.Public
                    ? prototype.PublicSourceId ?? publicSource?.Id ?? SeedContext.NewId("src")
                    : null,
            OrganizationId = prototype.OrganizationId ?? seed.Organization.Id,
            LibraryId = prototype.LibraryId ?? SeedContext.NewId("lib"),
            Trigger = prototype.Trigger,
            Status = prototype.Status,
            FilesTotal = prototype.FilesTotal,
            FilesAdded = prototype.FilesAdded,
            FilesUpdated = prototype.FilesUpdated,
            FilesMoved = prototype.FilesMoved,
            FilesSkipped = prototype.FilesSkipped,
            FilesDeleted = prototype.FilesDeleted,
            FilesFailed = prototype.FilesFailed,
            AuthFailure = prototype.AuthFailure,
            FailureMessage = prototype.FailureMessage,
            StartedAtUtc = prototype.StartedAtUtc ?? createdAt,
            CompletedAtUtc = prototype.CompletedAtUtc,
            UpdatedAtUtc = createdAt,
        };
    }

    private static T? TryFindLatest<T>(IReadOnlyList<object> entities)
    {
        foreach (var entity in Flatten(entities).Reverse())
        {
            if (entity is T typed)
            {
                return typed;
            }
        }

        return default;
    }

    private static T FindLatest<T>(IReadOnlyList<object> entities)
    {
        foreach (var entity in Flatten(entities).Reverse())
        {
            if (entity is T typed)
            {
                return typed;
            }
        }

        throw new InvalidOperationException(
            $"EntityGraph requires an entity of type {typeof(T).Name} earlier in the graph."
        );
    }

    private static IEnumerable<object> Flatten(IEnumerable<object> entities)
    {
        foreach (var entity in entities)
        {
            if (entity is string)
            {
                yield return entity;
                continue;
            }

            if (entity is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable.OfType<object>())
                {
                    foreach (var nested in Flatten([item]))
                    {
                        yield return nested;
                    }
                }

                continue;
            }

            yield return entity;
        }
    }
}
