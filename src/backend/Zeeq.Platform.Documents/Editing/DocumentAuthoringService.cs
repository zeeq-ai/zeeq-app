using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Auditing;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Ingest;

namespace Zeeq.Platform.Documents.Editing;

/// <summary>A document resolved from either local storage or a subscribed public source.</summary>
public sealed record AuthoringDocument
{
    /// <summary>Creates a result backed by exactly one document source.</summary>
    public AuthoringDocument(
        LibraryDocument? local,
        DocsPublicDocument? publicDocument,
        string? publicRepoUrl
    )
    {
        if ((local is null) == (publicDocument is null))
            throw new ArgumentException("Exactly one document source is required.");
        Local = local;
        Public = publicDocument;
        PublicRepoUrl = publicRepoUrl;
    }

    /// <summary>Locally owned or private-source document.</summary>
    public LibraryDocument? Local { get; }

    /// <summary>Shared public-source document.</summary>
    public DocsPublicDocument? Public { get; }

    /// <summary>Public repository URL, when applicable.</summary>
    public string? PublicRepoUrl { get; }

    /// <summary>Resolved path.</summary>
    public string Path => Local?.Path ?? Public!.Path;

    /// <summary>Resolved markdown.</summary>
    public string Content => Local?.Content ?? Public!.Content;

    /// <summary>Stored content hash.</summary>
    public string? ContentHash => Local?.ContentHash ?? Public?.ContentHash;

    /// <summary>Display title.</summary>
    public string Title => Local?.Title ?? Public!.Title;
}

/// <summary>Stable result categories for document authoring.</summary>
public enum AuthoringStatus
{
    /// <summary>A document was created.</summary>
    Created,

    /// <summary>A document already exists.</summary>
    Exists,

    /// <summary>An edit was applied.</summary>
    Applied,

    /// <summary>The document changed since the read.</summary>
    Stale,

    /// <summary>The document was not found.</summary>
    NotFound,

    /// <summary>The path or authoring input is invalid.</summary>
    InvalidInput,

    /// <summary>The source owns the document.</summary>
    NotEditable,

    /// <summary>The diff is invalid.</summary>
    InvalidDiff,

    /// <summary>The diff has insufficient context.</summary>
    ThinContext,

    /// <summary>The diff does not match.</summary>
    NoMatch,

    /// <summary>The diff matches multiple locations.</summary>
    Ambiguous,

    /// <summary>The hunks are out of order.</summary>
    OutOfOrder,

    /// <summary>The result would be empty.</summary>
    EmptyResult,

    /// <summary>The input or result exceeds a limit.</summary>
    TooLarge,

    /// <summary>The diff could not be applied safely.</summary>
    ApplyFailed,
}

/// <summary>Maps authoring outcomes to stable wire and telemetry codes.</summary>
public static class AuthoringStatusExtensions
{
    /// <summary>Returns a stable lowercase underscore-separated outcome code.</summary>
    public static string ToCode(this AuthoringStatus status) =>
        status switch
        {
            AuthoringStatus.Created => "created",
            AuthoringStatus.Exists => "exists",
            AuthoringStatus.Applied => "applied",
            AuthoringStatus.Stale => "stale",
            AuthoringStatus.NotFound => "not_found",
            AuthoringStatus.InvalidInput => "invalid_input",
            AuthoringStatus.NotEditable => "not_editable",
            AuthoringStatus.InvalidDiff => "invalid_diff",
            AuthoringStatus.ThinContext => "thin_context",
            AuthoringStatus.NoMatch => "no_match",
            AuthoringStatus.Ambiguous => "ambiguous",
            AuthoringStatus.OutOfOrder => "out_of_order",
            AuthoringStatus.EmptyResult => "empty_result",
            AuthoringStatus.TooLarge => "too_large",
            AuthoringStatus.ApplyFailed => "apply_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
}

/// <summary>Result of an authoring operation.</summary>
public sealed record AuthoringResult(
    AuthoringStatus Status,
    AuthoringDocument? Document = null,
    DiffOutcome? Outcome = null,
    string? PreviousHash = null,
    bool ListMayBeStale = false
);

/// <summary>Resolves and writes agent-authored library documents.</summary>
public sealed partial class DocumentAuthoringService(
    ILibraryDocumentStore store,
    IDocsPublicDocumentStore publicDocuments,
    IDocsPublicSourceStore publicSources,
    DocumentDiffEditor editor,
    ILogger<DocumentAuthoringService> logger,
    HybridCache? cache = null
)
{
    /// <summary>Maximum rationale length in UTF-16 characters.</summary>
    public const int MaxRationaleLength = 2000;

    /// <summary>Maximum stored MCP client name length in UTF-16 characters.</summary>
    public const int MaxClientNameLength = 256;

    /// <summary>Resolves visible public documents and local overlays.</summary>
    public async Task<AuthoringDocument?> ResolveAsync(
        string orgId,
        Library library,
        string path,
        CancellationToken ct
    )
    {
        var normalized = DocumentNormalizer.NormalizePath(path);
        DocsPublicDocument? shared = null;
        DocsPublicSource? source = null;
        if (library.PublicSourceId is { } sourceId)
        {
            source = await publicSources.GetByIdAsync(sourceId, ct)
                ?? throw new InvalidOperationException(
                    "The library's public source is unavailable."
                );
            shared = await publicDocuments.GetByPathAsync(sourceId, normalized, ct);
            if (
                shared is not null
                && !IngestFileFilter.IsIncluded(
                    shared.Path,
                    LibraryEndpointMapping.ResolveEffectiveFilter(library, source)
                )
            )
                shared = null;
        }
        var local = await store.GetByPathAsync(orgId, library.Id, normalized, ct);
        if (local is not null && local.Path == normalized)
        {
            if (shared is not null && shared.Path == normalized)
                LogPublicCollision(logger, library.Name, normalized);
            return new(local, null, null);
        }
        if (shared is not null && shared.Path == normalized)
            return new(null, shared, source!.RepoUrl);
        if (shared is not null)
            return new(null, shared, source!.RepoUrl);
        return local is null ? null : new(local, null, null);
    }

    /// <summary>Lists local documents and visible public-source documents without duplicate paths.</summary>
    public async Task<IReadOnlyList<LibraryDocument>> ListAsync(
        string orgId,
        Library library,
        CancellationToken ct
    )
    {
        var local = await store.ListDocumentsAsync(orgId, library.Id, ct);
        if (library.PublicSourceId is not { } sourceId)
            return local;
        var source = await publicSources.GetByIdAsync(sourceId, ct)
            ?? throw new InvalidOperationException("The library's public source is unavailable.");
        var filter = LibraryEndpointMapping.ResolveEffectiveFilter(library, source);
        var shared = (await publicDocuments.ListSummariesBySourceAsync(sourceId, ct))
            .Where(d => IngestFileFilter.IsIncluded(d.Path, filter))
            .Select(d => new LibraryDocument
            {
                Id = d.Id,
                OrganizationId = orgId,
                LibraryId = library.Id,
                Path = d.Path,
                Title = d.Title,
                TitleNormalized = d.TitleNormalized,
                Keywords = d.Keywords,
                Headings = d.Headings,
                Content = string.Empty,
                ContentHash = d.ContentHash,
                TokenCount = d.TokenCount,
                ProcessingStatus = d.ProcessingStatus,
                Authoring = LibraryDocumentAuthoring.Synced,
                SourceOrigin = new LibraryDocumentSourceOrigin("GitHub", source.RepoUrl),
                CreatedAt = d.CreatedAt,
                UpdatedAt = d.UpdatedAt,
            })
            .ToArray();
        var localPaths = local.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var item in shared.Where(d => localPaths.Contains(d.Path)))
            LogPublicCollision(logger, library.Name, item.Path);
        return [.. local, .. shared.Where(d => !localPaths.Contains(d.Path))];
    }

    /// <summary>Creates a new agent-owned document if no visible exact path exists.</summary>
    public async Task<AuthoringResult> CreateAsync(
        string orgId,
        Library library,
        string path,
        string content,
        string rationale,
        AuditActor actor,
        string? clientName,
        CancellationToken ct
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity("Zeeq.Documents.Create");
        activity?.SetTag("outcome", "error");
        activity?.SetTag("library", library.Name);
        activity?.SetTag("actor.id", actor.Id);
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleLength)
            return Finish(AuthoringStatus.InvalidInput);
        string normalized;
        try
        {
            normalized = DocumentNormalizer.NormalizePath(path);
        }
        catch (ArgumentException)
        {
            return Finish(AuthoringStatus.InvalidInput);
        }
        activity?.SetTag("document.path", normalized);
        if (string.IsNullOrWhiteSpace(content))
            return Finish(AuthoringStatus.EmptyResult);
        if (content.Length > 200_000)
            return Finish(AuthoringStatus.TooLarge);
        var found = await ResolveAsync(orgId, library, normalized, ct);
        if (found is not null && found.Path == normalized)
            return Finish(AuthoringStatus.Exists, found);
        var now = DateTimeOffset.UtcNow;
        var doc = new LibraryDocument
        {
            Id = $"document_{Guid.CreateVersion7():N}",
            OrganizationId = orgId,
            LibraryId = library.Id,
            Path = normalized,
            TeamId = null,
            CreatedAt = now,
        };
        DocumentContentPreparation.Apply(doc, content, now);
        var result = await store.CreateAgentDocumentAsync(
            doc,
            rationale,
            actor,
            LimitClientName(clientName),
            ct
        );
        if (result is CreateDocumentResult.Exists exists)
            return Finish(AuthoringStatus.Exists, new(exists.Document, null, null));
        LogCreated(logger, library.Name, normalized, doc.ContentHash, actor.Id, rationale.Length);
        var cacheFailed = false;
        if (cache is not null)
        {
            try
            {
                await cache.RemoveAsync(
                    McpDocumentIndexCache.Key(orgId, library.Id),
                    CancellationToken.None
                );
            }
            catch (Exception)
            {
                cacheFailed = true;
                LogIndexInvalidationFailed(logger, library.Name);
            }
        }
        return Finish(AuthoringStatus.Created, new(doc, null, null), cacheFailed);
        AuthoringResult Finish(
            AuthoringStatus status,
            AuthoringDocument? document = null,
            bool listMayBeStale = false
        )
        {
            activity?.SetTag("outcome", status.ToCode());
            return new(status, document, ListMayBeStale: listMayBeStale);
        }
    }

    /// <summary>Applies a guarded diff and saves it only when the base remains current.</summary>
    public async Task<AuthoringResult> UpdateAsync(
        string orgId,
        Library library,
        string path,
        string baseHash,
        string diff,
        string rationale,
        AuditActor actor,
        string? clientName,
        CancellationToken ct
    )
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity("Zeeq.Documents.Edit");
        activity?.SetTag("outcome", "error");
        activity?.SetTag("library", library.Name);
        activity?.SetTag("actor.id", actor.Id);
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleLength)
            return Finish(AuthoringStatus.InvalidInput);
        string normalized;
        try
        {
            normalized = DocumentNormalizer.NormalizePath(path);
        }
        catch (ArgumentException)
        {
            return Finish(AuthoringStatus.InvalidInput);
        }
        var found = await ResolveAsync(orgId, library, normalized, ct);
        if (found is null)
            return Finish(AuthoringStatus.NotFound);
        activity?.SetTag("document.path", found.Path);
        if (found.Public is not null)
            return Finish(
                AuthoringStatus.NotEditable,
                found,
                new DiffOutcome.NotEditable(
                    new LibraryDocumentSourceOrigin("GitHub", found.PublicRepoUrl!)
                )
            );
        var doc = found.Local!;
        var oldHash = doc.ContentHash;
        var outcome = await editor.ApplyAsync(new DiffContext(doc, baseHash, diff), ct);
        if (outcome is not DiffOutcome.Applied applied)
            return Finish(OutcomeName(outcome), found, outcome);
        // Do not mutate the resolved instance: it may be held by a cache or another caller.
        var replacement = new LibraryDocument
        {
            Id = doc.Id,
            OrganizationId = doc.OrganizationId,
            LibraryId = doc.LibraryId,
            TeamId = doc.TeamId,
            Path = doc.Path,
            CreatedAt = doc.CreatedAt,
            Authoring = doc.Authoring,
        };
        DocumentContentPreparation.Apply(replacement, applied.Content, DateTimeOffset.UtcNow);
        var saved = await store.TryReplaceContentAsync(
            replacement,
            baseHash,
            rationale,
            actor,
            LimitClientName(clientName),
            ct
        );
        switch (saved)
        {
            case ReplaceContentResult.Applied ok:
                LogUpdated(
                    logger,
                    library.Name,
                    ok.Document.Path,
                    oldHash,
                    ok.Document.ContentHash,
                    actor.Id,
                    rationale.Length
                );
                return Finish(
                    AuthoringStatus.Applied,
                    new(ok.Document, null, null),
                    new DiffOutcome.Applied(ok.Document.Content, ok.Document.ContentHash!),
                    oldHash
                );
            case ReplaceContentResult.Stale stale:
                return Finish(
                    AuthoringStatus.Stale,
                    new(stale.Document, null, null),
                    new DiffOutcome.Stale(stale.Document.Content, stale.Document.ContentHash)
                );
            case ReplaceContentResult.NotEditable denied:
                return Finish(
                    AuthoringStatus.NotEditable,
                    new(denied.Document, null, null),
                    new DiffOutcome.NotEditable(denied.Document.SourceOrigin)
                );
            case ReplaceContentResult.NotFound:
                return Finish(AuthoringStatus.NotFound);
            default:
                throw new InvalidOperationException("Unexpected authoring result.");
        }
        AuthoringResult Finish(
            AuthoringStatus status,
            AuthoringDocument? document = null,
            DiffOutcome? result = null,
            string? previousHash = null
        )
        {
            activity?.SetTag("outcome", status.ToCode());
            return new(status, document, result, previousHash);
        }
    }

    private static string? LimitClientName(string? value)
    {
        if (value is not { Length: > MaxClientNameLength })
            return value;
        var length =
            char.IsHighSurrogate(value[MaxClientNameLength - 1])
            && char.IsLowSurrogate(value[MaxClientNameLength])
                ? MaxClientNameLength - 1
                : MaxClientNameLength;
        return value[..length];
    }

    private static AuthoringStatus OutcomeName(DiffOutcome outcome) =>
        outcome switch
        {
            DiffOutcome.Applied => AuthoringStatus.Applied,
            DiffOutcome.Stale => AuthoringStatus.Stale,
            DiffOutcome.NotEditable => AuthoringStatus.NotEditable,
            DiffOutcome.InvalidDiff => AuthoringStatus.InvalidDiff,
            DiffOutcome.ThinContext => AuthoringStatus.ThinContext,
            DiffOutcome.NoMatch => AuthoringStatus.NoMatch,
            DiffOutcome.Ambiguous => AuthoringStatus.Ambiguous,
            DiffOutcome.OutOfOrder => AuthoringStatus.OutOfOrder,
            DiffOutcome.EmptyResult => AuthoringStatus.EmptyResult,
            DiffOutcome.TooLarge => AuthoringStatus.TooLarge,
            DiffOutcome.ApplyFailed => AuthoringStatus.ApplyFailed,
        };

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to invalidate document list for library {Library}"
    )]
    private static partial void LogIndexInvalidationFailed(ILogger logger, string library);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Local document owns path {Path} in subscribed library {Library}; shared source remains unchanged"
    )]
    private static partial void LogPublicCollision(ILogger logger, string library, string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Created agent document {Library} {Path} hash {Hash} actor {Actor} rationale length {RationaleLength}"
    )]
    private static partial void LogCreated(
        ILogger logger,
        string library,
        string path,
        string? hash,
        string actor,
        int rationaleLength
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Updated agent document {Library} {Path} hash {PreviousHash} to {NewHash} actor {Actor} rationale length {RationaleLength}"
    )]
    private static partial void LogUpdated(
        ILogger logger,
        string library,
        string path,
        string? previousHash,
        string? newHash,
        string actor,
        int rationaleLength
    );
}
