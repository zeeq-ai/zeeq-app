using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Danom;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Parsing;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest;

/// <summary>Executes one full or incremental Notion content ingest run.</summary>
/// <remarks>
/// Pages are processed sequentially: the scoped stores share one EF <c>DbContext</c>, and the
/// Notion client already enforces a low request rate. A full-run deletion sweep is permitted only
/// after every visible page was processed without failure.
/// </remarks>
public sealed partial class NotionIngestRunner(
    ILibraryDocumentStore libraries,
    IExternalPendingContentSyncStore pendingContent,
    IDocsIngestRunStore runStore,
    IngestSettings ingestSettings,
    ILogger<NotionIngestRunner> logger
)
{
    /// <summary>Runs one Notion ingest job to durable completion.</summary>
    public async Task<DocsIngestRun> RunAsync(
        NotionIngestJob job,
        IZeeqNotionClient client,
        CancellationToken cancellationToken
    )
    {
        ZeeqTelemetry.TryParseTraceContext(job.TraceContext, out var parentContext);
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.notion",
            ActivityKind.Internal,
            parentContext,
            tags:
            [
                new("organization.id", job.OrganizationId),
                new("library.id", job.LibraryId),
                new("ingest.scope", job.Scope.ToString()),
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
                SourceKind = RepositorySourceKind.Notion,
                RepoUrl = job.SourceReference,
                OrganizationId = job.OrganizationId,
                LibraryId = job.LibraryId,
                Trigger = job.Trigger,
                SyncScope = job.Scope,
                Status = IngestRunStatus.Running,
                RootTraceId = activity?.TraceId.ToString(),
                StartedAtUtc = startedAt,
                UpdatedAtUtc = startedAt,
            },
            cancellationToken
        );

        LogRunStarted(logger, job.RunId, job.LibraryId, job.Scope);
        var finalization = await ProcessPagesAsync(job, client, cancellationToken);

        await runStore.FinalizeAsync(
            job.RunId,
            job.RunCreatedAtUtc,
            finalization,
            cancellationToken
        );

        LogRunFinished(
            logger,
            job.RunId,
            job.LibraryId,
            job.Scope,
            finalization.Status,
            finalization.FilesTotal,
            finalization.FilesAdded,
            finalization.FilesUpdated,
            finalization.FilesMoved,
            finalization.FilesSkipped,
            finalization.FilesDeleted,
            finalization.FilesFailed
        );

        return await runStore.GetAsync(job.RunId, job.RunCreatedAtUtc, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Notion ingest run {job.RunId} was finalized but could not be re-read."
            );
    }

    private async Task<IngestRunFinalization> ProcessPagesAsync(
        NotionIngestJob job,
        IZeeqNotionClient client,
        CancellationToken cancellationToken
    )
    {
        var counts = new RunCounts();
        // NOTE: Full runs retain one NotionPage metadata entry per discovered page so parent
        // walks can resolve without repeatedly fetching ancestors. `pathOwners` separately keeps
        // one resolved path per processed page for in-run collision detection. Both are per-page,
        // not per block; bounding them needs a broader persistence-backed path ownership design.
        var resolver = new NotionPagePathResolver(client);
        var pathOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await foreach (var page in DiscoverPagesAsync(job, client, resolver, cancellationToken))
            {
                counts.Total++;
                try
                {
                    var outcome = await ProcessPageAsync(
                        job,
                        client,
                        resolver,
                        pathOwners,
                        page.PageId,
                        cancellationToken
                    );
                    counts.Add(outcome);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    counts.Failed++;
                    LogPageFailed(logger, job.RunId, page.PageId, ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFatalError(logger, job.RunId, ex);
            return counts.Finalize(IngestRunStatus.Failed, deleted: 0, failureMessage: ex.Message);
        }

        var swept = 0;
        if (job.Scope is ExternalSyncScope.Full && counts.Failed == 0)
        {
            swept = await libraries.DeleteUnstampedAsync(
                job.OrganizationId,
                job.LibraryId,
                job.RunId,
                cancellationToken
            );
        }

        return counts.Finalize(
            counts.Failed == 0 ? IngestRunStatus.Succeeded : IngestRunStatus.Partial,
            counts.Deleted + swept,
            counts.Failed == 0 ? null : $"{counts.Failed} page(s) failed to process."
        );
    }

    private async IAsyncEnumerable<DiscoveredNotionPage> DiscoverPagesAsync(
        NotionIngestJob job,
        IZeeqNotionClient client,
        NotionPagePathResolver resolver,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (job.Scope is ExternalSyncScope.Full)
        {
            await foreach (var page in client.SearchPagesAsync(cancellationToken))
            {
                resolver.Seed(page);
                yield return new DiscoveredNotionPage(page.PageId);
            }

            yield break;
        }

        var claimed = await pendingContent.ClaimAsync(
            job.OrganizationId,
            job.LibraryId,
            job.RunId,
            DateTimeOffset.UtcNow,
            ingestSettings.RunningSyncStaleAfter,
            cancellationToken
        );
        foreach (var page in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DiscoveredNotionPage(page.ExternalContentId);
        }
    }

    private async Task<PageOutcome> ProcessPageAsync(
        NotionIngestJob job,
        IZeeqNotionClient client,
        NotionPagePathResolver resolver,
        Dictionary<string, string> pathOwners,
        string pageId,
        CancellationToken cancellationToken
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.notion.page",
            ActivityKind.Internal,
            parentContext: default,
            tags: [new("notion.page.id", pageId)]
        );

        var resolvedLookup = ClassifyLookup(
            await resolver.ResolveAsync(pageId, cancellationToken),
            job.Scope
        );
        if (!resolvedLookup.TryGetT1(out var resolved))
        {
            return await HandleUnavailablePageAsync(resolvedLookup, job, pageId, cancellationToken);
        }

        var path = await ResolveUniquePathAsync(job, resolved, pathOwners, cancellationToken);
        activity?.SetTag("document.path", path);

        if (!IngestFileFilter.MatchesGlobs(path, job.Filter))
        {
            await ClearIncrementalAsync(job, pageId, cancellationToken);
            return PageOutcome.Skipped;
        }

        var markdownLookup = ClassifyLookup(
            await client.GetPageMarkdownAsync(pageId, cancellationToken),
            job.Scope
        );
        if (!markdownLookup.TryGetT1(out var markdown))
        {
            return await HandleUnavailablePageAsync(markdownLookup, job, pageId, cancellationToken);
        }

        if (markdown.Truncated)
        {
            activity?.SetTag("notion.markdown.truncated", true);
            activity?.SetTag("notion.markdown.unknown_block_count", markdown.UnknownBlockCount);
            LogTruncatedPage(logger, job.RunId, pageId, markdown.UnknownBlockCount);
        }

        if (
            job.Scope is ExternalSyncScope.Incremental
            && !await IsClaimStillActiveAsync(job, pageId, cancellationToken)
        )
        {
            // A page.deleted webhook removed this pending row (and the document) while this
            // run was mid-fetch; writing now would resurrect a page Notion says was deleted.
            return PageOutcome.Skipped;
        }

        var parsed = MarkdownParser.Parse(markdown.Markdown, resolved.Title);
        var now = DateTimeOffset.UtcNow;
        var result = await libraries.UpsertSyncedDocumentAsync(
            new LibraryDocument
            {
                Id = $"doc_{Guid.CreateVersion7():N}",
                OrganizationId = job.OrganizationId,
                TeamId = job.TeamId,
                LibraryId = job.LibraryId,
                Path = path,
                Title = parsed.Title,
                TitleNormalized = DocumentNormalizer.Normalize(parsed.Title),
                ParsedSkillName = DocumentNormalizer.NormalizeOptionalPromptName(
                    parsed.ParsedSkillName
                ),
                ParsedSkillDescription = DocumentNormalizer.BoundOptionalSkillDescription(
                    parsed.ParsedSkillDescription
                ),
                Keywords = DocumentNormalizer.NormalizeKeywords(parsed.Keywords),
                Headings = [.. parsed.Headings],
                Content = markdown.Markdown,
                ContentHash = ComputeSha256Hex(markdown.Markdown),
                TokenCount = TiktokenCounter.CountTokens(parsed.Content),
                SyncRunId = job.RunId,
                SourceExternalId = pageId,
                SourceOrigin = new LibraryDocumentSourceOrigin("Notion", job.SourceReference),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken
        );

        await ClearIncrementalAsync(job, pageId, cancellationToken);
        activity?.AddEvent([new("upsert.kind", result.Kind.ToString())]);
        return result.Kind switch
        {
            DocumentUpsertKind.Added => PageOutcome.Added,
            DocumentUpsertKind.Updated => PageOutcome.Updated,
            DocumentUpsertKind.Moved => PageOutcome.Moved,
            _ => PageOutcome.Skipped,
        };
    }

    /// <summary>
    /// Classifies a nullable upstream lookup (page resolution or Markdown fetch) into one of
    /// three discrete outcomes, so a transient read failure during a full resync can never be
    /// mistaken for an authoritative deletion.
    /// </summary>
    /// <remarks>
    /// Only an incremental job — driven by a webhook that already asserted the page is gone —
    /// may treat a null lookup as <see cref="PageAbsent"/>. A full resync's <c>SearchPagesAsync</c>
    /// just reported the page as visible, so a null result there means the read failed, not that
    /// the page disappeared; classifying it as <see cref="PageUnavailable"/> forces the caller to
    /// fail the page instead of deleting it and letting <c>DeleteUnstampedAsync</c> proceed.
    /// </remarks>
    private static Choice<T, PageAbsent, PageUnavailable> ClassifyLookup<T>(
        T? value,
        ExternalSyncScope scope
    )
        where T : class =>
        value is not null ? Choice<T, PageAbsent, PageUnavailable>.FromT1(value)
        : scope is ExternalSyncScope.Incremental
            ? Choice<T, PageAbsent, PageUnavailable>.FromT2(default)
        : Choice<T, PageAbsent, PageUnavailable>.FromT3(default);

    private async Task<PageOutcome> HandleUnavailablePageAsync<T>(
        Choice<T, PageAbsent, PageUnavailable> outcome,
        NotionIngestJob job,
        string pageId,
        CancellationToken cancellationToken
    ) =>
        outcome.TryGetT2(out _)
            ? await DeleteAbsentPageAsync(job, pageId, cancellationToken)
            : throw new InvalidOperationException(
                $"Notion page '{pageId}' was discovered but could not be retrieved."
            );

    private readonly record struct PageAbsent;

    private readonly record struct PageUnavailable;

    private async Task<PageOutcome> DeleteAbsentPageAsync(
        NotionIngestJob job,
        string pageId,
        CancellationToken cancellationToken
    )
    {
        var existing = await libraries.GetByExternalIdAsync(
            job.OrganizationId,
            job.LibraryId,
            pageId,
            cancellationToken
        );
        await libraries.DeleteDocumentByExternalIdAsync(
            job.OrganizationId,
            job.LibraryId,
            pageId,
            cancellationToken
        );
        await ClearIncrementalAsync(job, pageId, cancellationToken);
        return existing is null ? PageOutcome.Skipped : PageOutcome.Deleted;
    }

    private async Task ClearIncrementalAsync(
        NotionIngestJob job,
        string pageId,
        CancellationToken cancellationToken
    )
    {
        if (job.Scope is ExternalSyncScope.Incremental)
        {
            await pendingContent.ClearAsync(
                job.OrganizationId,
                job.LibraryId,
                pageId,
                job.RunId,
                cancellationToken
            );
        }
    }

    private Task<bool> IsClaimStillActiveAsync(
        NotionIngestJob job,
        string pageId,
        CancellationToken cancellationToken
    ) =>
        pendingContent.IsClaimActiveAsync(
            job.OrganizationId,
            job.LibraryId,
            pageId,
            job.RunId,
            cancellationToken
        );

    private async Task<string> ResolveUniquePathAsync(
        NotionIngestJob job,
        ResolvedNotionPage page,
        Dictionary<string, string> pathOwners,
        CancellationToken cancellationToken
    )
    {
        if (await IsPathAvailableAsync(job, page.Path, page.PageId, pathOwners, cancellationToken))
        {
            pathOwners[page.Path] = page.PageId;
            return page.Path;
        }

        var compactId = new string(
            page.PageId.Where(char.IsLetterOrDigit).ToArray()
        ).ToLowerInvariant();
        if (compactId.Length == 0)
        {
            compactId = ComputeSha256Hex(page.PageId);
        }

        foreach (var suffixLength in new[] { 8, 12, compactId.Length }.Distinct())
        {
            var suffix = compactId[..Math.Min(suffixLength, compactId.Length)];
            var candidate = $"{page.Path}--{suffix}";
            if (candidate.Length > NotionPagePathResolver.MaxPathLength)
            {
                throw new InvalidOperationException(
                    $"Disambiguated Notion path exceeds {NotionPagePathResolver.MaxPathLength} characters."
                );
            }

            if (
                await IsPathAvailableAsync(
                    job,
                    candidate,
                    page.PageId,
                    pathOwners,
                    cancellationToken
                )
            )
            {
                pathOwners[candidate] = page.PageId;
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not create a unique document path for Notion page '{page.PageId}'."
        );
    }

    private async Task<bool> IsPathAvailableAsync(
        NotionIngestJob job,
        string path,
        string pageId,
        Dictionary<string, string> pathOwners,
        CancellationToken cancellationToken
    )
    {
        if (pathOwners.TryGetValue(path, out var inRunOwner))
        {
            return inRunOwner == pageId;
        }

        // NOTE: Phase 5 keeps collision probing simple and sequential: one persisted path lookup
        // per processed page, plus rare suffix probes on collisions. A batched path map needs a
        // broader store API and can be added once real Notion library sizes justify it.
        var persisted = await libraries.GetByPathAsync(
            job.OrganizationId,
            job.LibraryId,
            path,
            cancellationToken
        );
        return persisted is null || persisted.SourceExternalId == pageId;
    }

    private static string ComputeSha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed record DiscoveredNotionPage(string PageId);

    private enum PageOutcome
    {
        Added = 0,
        Updated = 1,
        Moved = 2,
        Skipped = 3,
        Deleted = 4,
    }

    private sealed class RunCounts
    {
        internal int Total { get; set; }
        internal int Added { get; private set; }
        internal int Updated { get; private set; }
        internal int Moved { get; private set; }
        internal int Skipped { get; private set; }
        internal int Deleted { get; private set; }
        internal int Failed { get; set; }

        internal void Add(PageOutcome outcome)
        {
            switch (outcome)
            {
                case PageOutcome.Added:
                    Added++;
                    break;
                case PageOutcome.Updated:
                    Updated++;
                    break;
                case PageOutcome.Moved:
                    Moved++;
                    break;
                case PageOutcome.Skipped:
                    Skipped++;
                    break;
                case PageOutcome.Deleted:
                    Deleted++;
                    break;
            }
        }

        internal IngestRunFinalization Finalize(
            IngestRunStatus status,
            int deleted,
            string? failureMessage
        ) =>
            new(
                Status: status,
                FilesTotal: Total,
                FilesAdded: Added,
                FilesUpdated: Updated,
                FilesMoved: Moved,
                FilesSkipped: Skipped,
                FilesDeleted: deleted,
                FilesFailed: Failed,
                AuthFailure: false,
                FailureMessage: failureMessage,
                CompletedAtUtc: DateTimeOffset.UtcNow
            );
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Notion ingest run started. RunId={RunId}, LibraryId={LibraryId}, Scope={Scope}"
    )]
    private static partial void LogRunStarted(
        ILogger logger,
        string runId,
        string libraryId,
        ExternalSyncScope scope
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Notion ingest run finished. RunId={RunId}, LibraryId={LibraryId}, Scope={Scope}, Status={Status}, Total={Total}, Added={Added}, Updated={Updated}, Moved={Moved}, Skipped={Skipped}, Deleted={Deleted}, Failed={Failed}"
    )]
    private static partial void LogRunFinished(
        ILogger logger,
        string runId,
        string libraryId,
        ExternalSyncScope scope,
        IngestRunStatus status,
        int total,
        int added,
        int updated,
        int moved,
        int skipped,
        int deleted,
        int failed
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Notion page failed during ingest. RunId={RunId}, PageId={PageId}"
    )]
    private static partial void LogPageFailed(
        ILogger logger,
        string runId,
        string pageId,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Notion returned truncated Markdown. RunId={RunId}, PageId={PageId}, UnknownBlockCount={UnknownBlockCount}"
    )]
    private static partial void LogTruncatedPage(
        ILogger logger,
        string runId,
        string pageId,
        int unknownBlockCount
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Notion ingest run failed during page discovery. RunId={RunId}"
    )]
    private static partial void LogRunFatalError(ILogger logger, string runId, Exception ex);
}
