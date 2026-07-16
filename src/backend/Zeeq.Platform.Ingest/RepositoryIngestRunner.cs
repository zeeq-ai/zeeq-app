using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Core.Documents.Parsing;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Executes one repository ingest run against an already-acquired workspace.
/// </summary>
/// <remarks>
/// This is the runner core described in spec §8 — pure business logic with no
/// dispatcher or workspace-acquisition concerns (those live in
/// <c>IRepositoryIngestDispatcher</c> / <c>IIngestWorkspaceProvider</c>
/// implementations). The runner opens a run record, walks every in-scope file
/// under <see cref="IIngestWorkspace.RootPath"/>, upserts each one through the
/// document store with per-file failure isolation, runs the deletion sweep only
/// on a clean pass, and finalizes the run record.
/// <para>
/// <b>Both source kinds supported.</b> Public jobs write through
/// <see cref="IDocsPublicDocumentStore"/> into <c>docs_public_documents</c>;
/// private jobs write through <see cref="ILibraryDocumentStore"/>'s
/// <c>UpsertSyncedDocumentAsync</c>/<c>DeleteUnstampedAsync</c> into
/// <c>docs_library_documents</c> — the same move-detection-by-content-hash
/// shape, scoped by <c>(organization_id, library_id)</c> instead of
/// <c>public_source_id</c>. File discovery, filtering, hashing, and per-file
/// failure isolation are identical for both; only the destination store and
/// the entity shape being written differ.
/// </para>
/// <para>
/// <b>Concurrency:</b> files are processed sequentially, not with
/// <c>Parallel.ForEachAsync</c> as spec §8 sketches. The store layer is
/// registered scoped-per-request against one shared <c>DbContext</c>, which is
/// not safe to call concurrently from multiple tasks; spec §8 calls for each
/// worker to use its own <c>DbContext</c> from an <c>IDbContextFactory</c>,
/// which needs a factory-shaped store abstraction this slice does not yet
/// have. Sequential processing is correct and fully tested; bounded
/// parallelism is a deliberate, tracked follow-up once that seam exists.
/// </para>
/// </remarks>
public sealed partial class RepositoryIngestRunner(
    IDocsPublicDocumentStore publicDocumentStore,
    ILibraryDocumentStore libraryDocumentStore,
    IDocsIngestRunStore runStore,
    ILogger<RepositoryIngestRunner> logger,
    Func<string, CancellationToken, Task<string>>? readFile = null
)
{
    // NOTE: the file read is isolated behind a Func rather than calling
    // File.ReadAllTextAsync directly, per the side-effect-isolation pattern —
    // tests inject a failing Func to exercise per-file failure isolation
    // deterministically instead of relying on OS-specific filesystem tricks
    // (locks, broken symlinks) to force a real I/O error.
    private readonly Func<string, CancellationToken, Task<string>> _readFile =
        readFile ?? ((path, ct) => File.ReadAllTextAsync(path, Encoding.UTF8, ct));

    /// <summary>
    /// Runs one repository ingest job to completion and returns the finalized run record.
    /// </summary>
    /// <param name="job">The job to execute — repo, source identity, effective filter, trigger.</param>
    /// <param name="workspace">
    /// An already-acquired workspace whose <see cref="IIngestWorkspace.RootPath"/>
    /// contains the checked-out repository. Acquiring and cleaning up the
    /// workspace is the dispatcher's responsibility, not the runner's.
    /// </param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. Per-file work checks this between files; an
    /// in-flight file finishes rather than being torn down mid-write.
    /// </param>
    /// <returns>The finalized <see cref="DocsIngestRun"/> record.</returns>
    public async Task<DocsIngestRun> RunAsync(
        RepositoryIngestJob job,
        IIngestWorkspace workspace,
        CancellationToken cancellationToken
    )
    {
        switch (job.Kind)
        {
            case RepositorySourceKind.Public when job.PublicSourceId is null:
                throw new ArgumentException("Public jobs require PublicSourceId.", nameof(job));
            case RepositorySourceKind.Private
                when job.OrganizationId is null || job.LibraryId is null:
                throw new ArgumentException(
                    "Private jobs require OrganizationId and LibraryId.",
                    nameof(job)
                );
        }

        ZeeqTelemetry.TryParseTraceContext(job.TraceContext, out var parentContext);

        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.repository",
            ActivityKind.Internal,
            parentContext,
            tags:
            [
                new("repo.url", job.RepoUrl),
                new("source.kind", job.Kind.ToString()),
                new("ingest.trigger", job.Trigger.ToString()),
                new("run.id", job.RunId),
            ]
        );

        var startedAt = DateTimeOffset.UtcNow;

        await runStore.CreateAsync(
            new DocsIngestRun
            {
                Id = job.RunId,
                CreatedAtUtc = job.RunCreatedAtUtc,
                SourceKind = job.Kind,
                RepoUrl = job.RepoUrl,
                PublicSourceId = job.PublicSourceId,
                OrganizationId = job.OrganizationId,
                LibraryId = job.LibraryId,
                Trigger = job.Trigger,
                Status = IngestRunStatus.Running,
                RootTraceId = activity?.TraceId.ToString(),
                StartedAtUtc = startedAt,
                UpdatedAtUtc = startedAt,
            },
            cancellationToken
        );

        LogRunStarted(logger, job.RunId, job.RepoUrl);

        var finalization = await ProcessFilesAsync(job, workspace, cancellationToken);

        await runStore.FinalizeAsync(
            job.RunId,
            job.RunCreatedAtUtc,
            finalization,
            cancellationToken
        );

        LogRunFinished(
            logger,
            job.RunId,
            job.RepoUrl,
            finalization.Status,
            finalization.CompletedAtUtc - startedAt,
            finalization.FilesTotal,
            finalization.FilesAdded,
            finalization.FilesUpdated,
            finalization.FilesMoved,
            finalization.FilesSkipped,
            finalization.FilesDeleted,
            finalization.FilesFailed,
            finalization.AuthFailure
        );

        return await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Ingest run {job.RunId} was finalized but could not be re-read."
            );
    }

    /// <summary>
    /// Walks every in-scope file, upserts it, and runs the deletion sweep on a clean pass.
    /// </summary>
    /// <remarks>
    /// Isolated from <see cref="RunAsync"/> so a fatal error here (enumeration
    /// failure, workspace missing) is caught in one place and mapped to
    /// <see cref="IngestRunStatus.Failed"/> — distinct from per-file failures,
    /// which never fail the whole run (spec §8: "failed" is reserved for a
    /// fatal error that prevented any work, not for individual file failures).
    /// </remarks>
    private async Task<IngestRunFinalization> ProcessFilesAsync(
        RepositoryIngestJob job,
        IIngestWorkspace workspace,
        CancellationToken cancellationToken
    )
    {
        var total = 0;
        var added = 0;
        var updated = 0;
        var moved = 0;
        var skipped = 0;
        var failed = 0;

        // Tracks every normalized path seen this run so two on-disk files that differ only by
        // case (legal in git, e.g. `Docs/Guide.md` and `docs/guide.md`) — which now collide
        // under NormalizePath's case-folding — are caught and skipped instead of one silently
        // overwriting the other's row via the exact-path upsert match.
        var seenNormalizedPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await foreach (
                var file in EnumerateFilesAsync(workspace.RootPath, job.Filter, cancellationToken)
            )
            {
                total++;

                var normalizedPath = NormalizePath(file.RelativePath);

                if (!seenNormalizedPaths.Add(normalizedPath))
                {
                    failed++;
                    LogCaseCollision(logger, job.RunId, file.RelativePath, normalizedPath);
                    continue;
                }

                using var fileActivity = ZeeqTelemetry.Trace(
                    [("file.path", file.RelativePath)],
                    "ingest.file"
                );

                try
                {
                    var kind = await UpsertFileAsync(job, file, normalizedPath, cancellationToken);

                    switch (kind)
                    {
                        case DocumentUpsertKind.Added:
                            added++;
                            break;
                        case DocumentUpsertKind.Updated:
                            updated++;
                            break;
                        case DocumentUpsertKind.Moved:
                            moved++;
                            break;
                        case DocumentUpsertKind.Unchanged:
                            skipped++;
                            break;
                    }

                    fileActivity.AddEvent([("upsert.kind", kind.ToString())]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    LogFileFailed(logger, job.RunId, file.RelativePath, ex);
                    fileActivity.AddEvent([("error", ex.Message)]);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFatalError(logger, job.RunId, ex);

            return new IngestRunFinalization(
                Status: IngestRunStatus.Failed,
                FilesTotal: total,
                FilesAdded: added,
                FilesUpdated: updated,
                FilesMoved: moved,
                FilesSkipped: skipped,
                FilesDeleted: 0,
                FilesFailed: failed,
                AuthFailure: false,
                FailureMessage: ex.Message,
                CompletedAtUtc: DateTimeOffset.UtcNow
            );
        }

        // Clean pass — every touched row carries this run's stamp, so anything
        // still stamped with an older run is genuinely absent upstream.
        var deleted = failed == 0 ? await DeleteUnstampedAsync(job, cancellationToken) : 0;

        return new IngestRunFinalization(
            Status: failed == 0 ? IngestRunStatus.Succeeded : IngestRunStatus.Partial,
            FilesTotal: total,
            FilesAdded: added,
            FilesUpdated: updated,
            FilesMoved: moved,
            FilesSkipped: skipped,
            FilesDeleted: deleted,
            FilesFailed: failed,
            AuthFailure: false,
            FailureMessage: failed == 0 ? null : $"{failed} file(s) failed to process.",
            CompletedAtUtc: DateTimeOffset.UtcNow
        );
    }

    /// <summary>Routes the clean-pass deletion sweep to the right store for the job's kind.</summary>
    private Task<int> DeleteUnstampedAsync(RepositoryIngestJob job, CancellationToken ct) =>
        job.Kind == RepositorySourceKind.Public
            ? publicDocumentStore.DeleteUnstampedAsync(job.PublicSourceId!, job.RunId, ct)
            : libraryDocumentStore.DeleteUnstampedAsync(
                job.OrganizationId!,
                job.LibraryId!,
                job.RunId,
                ct
            );

    /// <summary>
    /// Reads, hashes, parses, and upserts one file, returning the resolved upsert branch.
    /// </summary>
    private async Task<DocumentUpsertKind> UpsertFileAsync(
        RepositoryIngestJob job,
        IngestFileEntry file,
        string normalizedPath,
        CancellationToken cancellationToken
    )
    {
        var content = await _readFile(file.AbsolutePath, cancellationToken);
        var fileName = Path.GetFileNameWithoutExtension(file.RelativePath);
        var parsed = MarkdownParser.Parse(content, fileName);
        var now = DateTimeOffset.UtcNow;

        if (
            !normalizedPath.Equals(
                "/" + file.RelativePath.Replace('\\', '/').TrimStart('/'),
                StringComparison.Ordinal
            )
        )
        {
            LogPathCaseFolded(logger, job.RunId, file.RelativePath, normalizedPath);
        }

        // Computed once and shared by both branches below — the public/private
        // entities differ only in identity/scope fields (Id, org/team/library
        // vs. public source id), never in how content is parsed or hashed.
        var fields = new ParsedDocumentFields(
            Path: normalizedPath,
            Title: parsed.Title,
            TitleNormalized: DocumentNormalizer.Normalize(parsed.Title),
            Keywords: DocumentNormalizer.NormalizeKeywords(parsed.Keywords),
            Headings: [.. parsed.Headings],
            Content: content,
            ContentHash: ComputeSha256Hex(content),
            TokenCount: TiktokenCounter.CountTokens(parsed.Content)
        );

        if (job.Kind == RepositorySourceKind.Private)
        {
            var libraryDocument = new LibraryDocument
            {
                Id = $"doc_{Guid.CreateVersion7():N}",
                OrganizationId = job.OrganizationId!,
                TeamId = job.TeamId,
                LibraryId = job.LibraryId!,
                Path = fields.Path,
                Title = fields.Title,
                TitleNormalized = fields.TitleNormalized,
                Keywords = fields.Keywords,
                Headings = fields.Headings,
                Content = fields.Content,
                ContentHash = fields.ContentHash,
                TokenCount = fields.TokenCount,
                SyncRunId = job.RunId,
                // SourceOrigin is what LibraryEndpointMapping's OriginOf uses to
                // mark a document "remote" (read-only in the editor) vs. "local"
                // (hand-authored, editable). Init-only on LibraryDocument, so
                // this only takes effect on first insert — an update to an
                // already-ingested row (the ContentHash-differs branch in
                // UpsertSyncedDocumentAsync) never touches it, which is correct:
                // it was already stamped remote on its original insert.
                SourceOrigin = new LibraryDocumentSourceOrigin("GitHub", job.RepoUrl),
                CreatedAt = now,
                UpdatedAt = now,
            };

            var libraryResult = await libraryDocumentStore.UpsertSyncedDocumentAsync(
                libraryDocument,
                cancellationToken
            );

            return libraryResult.Kind;
        }

        var document = new DocsPublicDocument
        {
            Id = $"doc_{Guid.CreateVersion7():N}",
            PublicSourceId = job.PublicSourceId!,
            Path = fields.Path,
            Title = fields.Title,
            TitleNormalized = fields.TitleNormalized,
            Keywords = fields.Keywords,
            Headings = fields.Headings,
            Content = fields.Content,
            ContentHash = fields.ContentHash,
            TokenCount = fields.TokenCount,
            SyncRunId = job.RunId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await publicDocumentStore.UpsertAsync(document, cancellationToken);

        return result.Kind;
    }

    /// <summary>
    /// Streams every Markdown-family file under <paramref name="rootPath"/> that matches <paramref name="filter"/>.
    /// </summary>
    /// <remarks>
    /// Bounded-memory by construction — files are yielded one at a time and
    /// never materialized as a full list, per spec §8's streaming requirement.
    /// <para>
    /// This walks the workspace's on-disk tree, which by the time the runner
    /// sees it has already been narrowed by <c>LocalTempWorkspaceProvider</c>'s
    /// git-level sparse checkout to just the three Markdown extensions —
    /// enumeration here is not a full-repository walk; sparse checkout already
    /// did that reduction at clone time. The one traversal cost this method
    /// does avoid on its own is recursing into <c>.git/</c> (packed objects,
    /// refs, etc.), which <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// with <see cref="SearchOption.AllDirectories"/> would otherwise walk;
    /// <see cref="EnumerateFileSystemPaths"/> skips that subtree explicitly.
    /// </para>
    /// </remarks>
    // No real awaiting happens below — the consumer (ProcessFilesAsync) already
    // does real async I/O per file, so a forced per-item Task.Yield() here just
    // adds a scheduler hop with no fairness benefit. `async` is still required
    // for the method to return IAsyncEnumerable<T> with EnumeratorCancellation.
#pragma warning disable CS1998
    private static async IAsyncEnumerable<IngestFileEntry> EnumerateFilesAsync(
        string rootPath,
        EffectiveFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var absolutePath in EnumerateFileSystemPaths(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootPath, absolutePath);
            if (!IngestFileFilter.IsIncluded(relativePath, filter))
            {
                continue;
            }

            yield return new IngestFileEntry(relativePath, absolutePath);
        }
    }
#pragma warning restore CS1998

    /// <summary>
    /// Recursively enumerates files under <paramref name="directory"/>,
    /// skipping <c>.git</c> subtrees so the walk never touches git's internal
    /// object store.
    /// </summary>
    private static IEnumerable<string> EnumerateFileSystemPaths(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(subdirectory) == ".git")
            {
                continue;
            }

            foreach (var file in EnumerateFileSystemPaths(subdirectory))
            {
                yield return file;
            }
        }
    }

    // NOTE: must lower-case, matching Zeeq.Core.Documents.DocumentNormalizer.NormalizePath's
    // identity convention. ILibraryDocumentStore.GetByPathAsync lower-cases the caller's input
    // before comparing against the stored Path column (case-sensitive btree), so any ingested
    // path that keeps its on-disk casing (e.g. a repo's README.md) can never be found by the
    // content-lookup endpoint — it 404s even though the document exists and is listed in the
    // tree. This normalizer can't reuse DocumentNormalizer.NormalizePath directly: that helper
    // unconditionally appends ".md" to anything not already ending in it, which would corrupt
    // ingested .mdx/.mdc paths.
    private static string NormalizePath(string relativePath) =>
        ("/" + relativePath.Replace('\\', '/').TrimStart('/')).ToLowerInvariant();

    private static string ComputeSha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>
    /// Parsed/hashed content shared by both <see cref="LibraryDocument"/> and
    /// <see cref="DocsPublicDocument"/> — the two entities differ only in
    /// identity/scope fields, never in how content is parsed or hashed.
    /// </summary>
    private readonly record struct ParsedDocumentFields(
        string Path,
        string Title,
        string TitleNormalized,
        string[] Keywords,
        string[] Headings,
        string Content,
        string ContentHash,
        int TokenCount
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Ingest run started. RunId={RunId}, RepoUrl={RepoUrl}"
    )]
    private static partial void LogRunStarted(ILogger logger, string runId, string repoUrl);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "📊  Ingest run finished. RunId={RunId}, RepoUrl={RepoUrl}, Status={Status}, Duration={Duration}, Total={FilesTotal}, Added={FilesAdded}, Updated={FilesUpdated}, Moved={FilesMoved}, Skipped={FilesSkipped}, Deleted={FilesDeleted}, Failed={FilesFailed}, AuthFailure={AuthFailure}"
    )]
    private static partial void LogRunFinished(
        ILogger logger,
        string runId,
        string repoUrl,
        IngestRunStatus status,
        TimeSpan duration,
        int filesTotal,
        int filesAdded,
        int filesUpdated,
        int filesMoved,
        int filesSkipped,
        int filesDeleted,
        int filesFailed,
        bool authFailure
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ingest file failed. RunId={RunId}, Path={Path}"
    )]
    private static partial void LogFileFailed(
        ILogger logger,
        string runId,
        string path,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Ingest run failed fatally before any deletion sweep could run. RunId={RunId}"
    )]
    private static partial void LogRunFatalError(ILogger logger, string runId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ingest path case-folded to its stored identity. RunId={RunId}, SourcePath={SourcePath}, StoredPath={StoredPath}"
    )]
    private static partial void LogPathCaseFolded(
        ILogger logger,
        string runId,
        string sourcePath,
        string storedPath
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ingest path collision after case-folding: two on-disk files normalize to the same stored identity, so the later one is skipped rather than silently overwriting the earlier one. RunId={RunId}, SourcePath={SourcePath}, NormalizedPath={NormalizedPath}"
    )]
    private static partial void LogCaseCollision(
        ILogger logger,
        string runId,
        string sourcePath,
        string normalizedPath
    );
}
