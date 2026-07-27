using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Mcp;

/// <summary>
/// Exposes organization-scoped library documents as dynamic MCP prompts.
/// </summary>
/// <remarks>
/// The MCP SDK invokes prompt handlers outside the normal Zeeq document endpoints, so this
/// service is the explicit boundary between an authenticated MCP request and the document store.
/// It resolves the caller's organization from the <see cref="ClaimsPrincipal"/>, lists documents
/// marked as <see cref="LibraryDocumentScopedSkill.Organization"/>, and maps those documents into
/// Model Context Protocol prompt payloads.
/// </remarks>
internal sealed class DynamicPromptsService(
    ILibraryDocumentStore documentStore,
    IHttpContextAccessor httpContextAccessor,
    ILogger<DynamicPromptsService> logger
) : IDynamicPromptsService
{
    /// <summary>
    /// Counts MCP prompt list calls by organization/result so prompt discovery can be monitored.
    /// </summary>
    private static readonly Counter<int> PromptListCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_dynamic_prompt_list_counter");

    /// <summary>
    /// Counts MCP prompt retrieval calls by organization/result/prompt name.
    /// </summary>
    private static readonly Counter<int> PromptGetCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_dynamic_prompt_get_counter");

    /// <summary>
    /// Counts successful prompt document retrievals for the Home → Prompts dashboard tab.
    /// </summary>
    /// <remarks>
    /// This counter is intentionally success-only. <see cref="PromptGetCounter" /> keeps the
    /// result-tagged diagnostics shape for failed lookups, while dashboard usage panels should
    /// answer "which skills were actually retrieved?" without mixing in validation and not-found
    /// attempts.
    /// </remarks>
    private static readonly Counter<int> PromptUsageCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_prompt_get_counter",
            "Successful dynamic MCP prompt document retrievals."
        );

    /// <summary>
    /// Lists dynamic prompts available to the authenticated caller's organization.
    /// </summary>
    /// <remarks>
    /// MCP clients use this as their discovery step. Only organization-scoped skill documents are
    /// surfaced for now; future scope values can be added in the document store query without
    /// changing the MCP handler shape.
    /// </remarks>
    public async ValueTask<ListPromptsResult> ListPromptsAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken
    )
    {
        var identity = ResolveIdentity(user);
        if (identity is null)
        {
            RecordListTelemetry(
                user,
                organizationId: null,
                promptCount: 0,
                result: "missing_identity"
            );
            return new ListPromptsResult { Prompts = [] };
        }

        using var activity = ZeeqTelemetry.Trace(
            [("organization_id", identity.OrganizationId)],
            "mcp.dynamic_prompts.list"
        );

        var documents = await documentStore.ListScopedSkillDocumentsAsync(
            identity.OrganizationId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );
        var entries = BuildPromptEntries(documents);

        RecordListTelemetry(
            user,
            identity.OrganizationId,
            promptCount: entries.Count,
            result: "success"
        );
        activity?.SetTag("prompt.count", entries.Count);

        return new ListPromptsResult
        {
            Prompts =
            [
                .. entries.Select(entry => new Prompt
                {
                    Name = entry.Name,
                    Title = DisplayTitle(entry.Document),
                    Description = Description(entry.Document),
                    Meta = Metadata(entry.Document),
                }),
            ],
        };
    }

    /// <summary>
    /// Retrieves a single prompt document and returns its markdown content as an MCP user message.
    /// </summary>
    /// <remarks>
    /// The MCP protocol addresses prompts by name, but Zeeq stores documents by organization,
    /// library, and document id. The first lookup resolves the requested prompt name to a stable
    /// document identity; the second lookup loads content for that exact scoped document.
    /// </remarks>
    public async ValueTask<GetPromptResult> GetPromptAsync(
        ClaimsPrincipal? user,
        GetPromptRequestParams? request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            RecordGetTelemetry(
                user,
                organizationId: null,
                promptName: null,
                document: null,
                "missing_name"
            );
            throw new McpProtocolException(
                "Missing required prompt name.",
                McpErrorCode.InvalidParams
            );
        }

        var identity = ResolveIdentity(user);
        if (identity is null)
        {
            RecordGetTelemetry(
                user,
                organizationId: null,
                promptName: request.Name,
                document: null,
                "missing_identity"
            );
            throw new McpProtocolException(
                "Unknown prompt: '" + request.Name + "'",
                McpErrorCode.InvalidParams
            );
        }

        using var activity = ZeeqTelemetry.Trace(
            [("organization_id", identity.OrganizationId), ("prompt.name", request.Name)],
            "mcp.dynamic_prompts.get"
        );

        var promptEntry = await ResolvePromptEntryAsync(
            identity.OrganizationId,
            request.Name,
            cancellationToken
        );
        if (promptEntry is null)
        {
            RecordGetTelemetry(
                user,
                identity.OrganizationId,
                request.Name,
                document: null,
                result: "not_found"
            );
            logger.LogInformation(
                "Dynamic MCP prompt {PromptName} was not found for organization {OrganizationId}.",
                request.Name,
                identity.OrganizationId
            );
            throw new McpProtocolException(
                "Unknown prompt: '" + request.Name + "'",
                McpErrorCode.InvalidParams
            );
        }

        var document = await documentStore.GetScopedSkillDocumentAsync(
            identity.OrganizationId,
            promptEntry.Document.LibraryId,
            promptEntry.Document.DocumentId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );
        if (document is null)
        {
            RecordGetTelemetry(
                user,
                identity.OrganizationId,
                request.Name,
                promptEntry.Document,
                result: "not_found"
            );
            throw new McpProtocolException(
                "Unknown prompt: '" + request.Name + "'",
                McpErrorCode.InvalidParams
            );
        }

        RecordGetTelemetry(user, identity.OrganizationId, request.Name, document, "success");
        RecordPromptUsageTelemetry(user, identity.OrganizationId, promptEntry.Name, document);
        ZeeqTelemetry.SetTags(
            ("document.id", document.DocumentId),
            ("document.path", document.Path),
            ("library.id", document.LibraryId),
            ("library.name", document.LibraryName)
        );

        return new GetPromptResult
        {
            Description = Description(document),
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = document.Content ?? string.Empty },
                },
            ],
        };
    }

    /// <summary>
    /// Resolves a prompt name to the matching scoped document projection.
    /// </summary>
    /// <remarks>
    /// Prompt names can come from three aliases: manual override, parsed front-matter name, or
    /// document path. Resolution uses the indexed store lookup first for normal get calls, then
    /// falls back to the list-shaped resolver so duplicate display-name suffixes and legacy
    /// normalization edge cases remain retrievable. Ambiguous aliases are treated as not found
    /// rather than guessing the wrong document.
    /// </remarks>
    private async Task<PromptEntry?> ResolvePromptEntryAsync(
        string organizationId,
        string promptName,
        CancellationToken cancellationToken
    )
    {
        var normalizedPromptName = DocumentNormalizer.NormalizePromptName(promptName);
        if (!string.IsNullOrEmpty(normalizedPromptName))
        {
            var indexedDocument = await documentStore.ResolveScopedSkillDocumentAsync(
                organizationId,
                normalizedPromptName,
                LibraryDocumentScopedSkill.Organization,
                cancellationToken
            );
            if (indexedDocument is not null)
            {
                return new PromptEntry(
                    normalizedPromptName,
                    PromptAliases(indexedDocument),
                    indexedDocument
                );
            }
        }

        // NOTE: Keep this fallback even though the common path is indexed above. The persisted
        // skill-name columns store authored values, while MCP list output normalizes names and may
        // append a suffix for duplicate display names. Loading the list here preserves retrieval
        // for those compatibility edge cases without forcing every get call through a full scan.
        var documents = await documentStore.ListScopedSkillDocumentsAsync(
            organizationId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );

        var entries = BuildPromptEntries(documents);
        var listedMatch = entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, promptName, StringComparison.Ordinal)
        );
        if (listedMatch is not null)
        {
            return listedMatch;
        }

        for (var priority = 0; priority <= 2; priority++)
        {
            var matches = entries
                .Where(entry =>
                    entry.Aliases.Any(alias =>
                        alias.Priority == priority
                        && string.Equals(alias.Name, normalizedPromptName, StringComparison.Ordinal)
                    )
                )
                .Take(2)
                .ToArray();

            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the minimal Zeeq identity required for organization-scoped prompt access.
    /// </summary>
    /// <remarks>
    /// The MCP filter supplies the request <see cref="ClaimsPrincipal"/>. Keeping claim extraction
    /// here ensures callers do not need to know Zeeq's claim names and prevents dynamic prompts
    /// from accidentally crossing organization boundaries.
    /// </remarks>
    private static ZeeqMinimalIdentity? ResolveIdentity(ClaimsPrincipal? user)
    {
        if (user?.AuthenticatedUser() is null)
        {
            return null;
        }

        var identity = user.AsZeeqMinimalIdentity();
        return string.IsNullOrWhiteSpace(identity.OrganizationId) ? null : identity;
    }

    /// <summary>
    /// Builds the prompt list returned to MCP clients, including collision-safe display names.
    /// </summary>
    /// <remarks>
    /// The first alias is the name exposed in <c>prompts/list</c>. If multiple documents produce
    /// the same exposed name, the listed name gets a short document-id suffix so clients can still
    /// retrieve each prompt deterministically.
    /// </remarks>
    private static IReadOnlyList<PromptEntry> BuildPromptEntries(
        IReadOnlyList<LibraryScopedSkillDocument> documents
    )
    {
        var baseNames = documents
            .Select(document =>
            {
                var aliases = PromptAliases(document);

                return new PromptEntry(aliases[0].Name, aliases, document);
            })
            .ToArray();

        var duplicateNames = baseNames
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (duplicateNames.Count == 0)
        {
            return baseNames;
        }

        return
        [
            .. baseNames.Select(entry =>
                duplicateNames.Contains(entry.Name)
                    ? entry with
                    {
                        Name = $"{entry.Name}-{ShortDocumentId(entry.Document.DocumentId)}",
                    }
                    : entry
            ),
        ];
    }

    /// <summary>
    /// Computes the path-based fallback prompt name from the document's final path segment.
    /// </summary>
    /// <remarks>
    /// This fallback keeps existing documents usable as prompts before parsed or manually-entered
    /// skill names exist. Example: <c>/backend/dotnet-csharp-best-practices.md</c> becomes
    /// <c>dotnet-csharp-best-practices</c>.
    /// </remarks>
    private static string BasePromptName(LibraryScopedSkillDocument document)
    {
        var segment = document
            .Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        var fileName = Path.GetFileNameWithoutExtension(segment ?? document.Path);
        var normalized = DocumentNormalizer.NormalizePromptName(fileName);

        return string.IsNullOrEmpty(normalized)
            ? $"document-{ShortDocumentId(document.DocumentId)}"
            : normalized;
    }

    /// <summary>
    /// Produces all normalized prompt-name aliases for a skill document in lookup priority order.
    /// </summary>
    /// <remarks>
    /// Priority order is business behavior: manual override wins, parsed front-matter follows,
    /// and path-derived name is the compatibility fallback. Empty or duplicate aliases are dropped
    /// so each document has a compact, ordered alias set.
    /// </remarks>
    private static IReadOnlyList<PromptAlias> PromptAliases(LibraryScopedSkillDocument document)
    {
        var aliases = new List<PromptAlias>(capacity: 3);
        AddAlias(aliases, priority: 0, document.ManualSkillName);
        AddAlias(aliases, priority: 1, document.ParsedSkillName);
        AddAlias(aliases, priority: 2, BasePromptName(document));

        return aliases;
    }

    /// <summary>
    /// Adds one normalized alias when the source value is present and unique for the document.
    /// </summary>
    private static void AddAlias(List<PromptAlias> aliases, int priority, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = DocumentNormalizer.NormalizePromptName(value);
        if (
            string.IsNullOrEmpty(normalized)
            || aliases.Any(alias => string.Equals(alias.Name, normalized, StringComparison.Ordinal))
        )
        {
            return;
        }

        aliases.Add(new PromptAlias(priority, normalized));
    }

    /// <summary>
    /// Returns a short suffix used to disambiguate duplicate prompt names in discovery results.
    /// </summary>
    private static string ShortDocumentId(string documentId) =>
        documentId.Length <= 8 ? documentId : documentId[^8..];

    /// <summary>
    /// Returns the user-facing prompt title.
    /// </summary>
    /// <remarks>
    /// This still honors the existing JSON metadata title override until the future skill metadata
    /// editing surface moves prompt presentation entirely onto top-level document fields.
    /// </remarks>
    private static string DisplayTitle(LibraryScopedSkillDocument document) =>
        string.IsNullOrWhiteSpace(document.Metadata?.TitleOverride)
            ? document.Title
            : document.Metadata.TitleOverride;

    /// <summary>
    /// Returns the prompt description shown to MCP clients.
    /// </summary>
    /// <remarks>
    /// Manual description is future user-entered metadata, parsed description comes from imported
    /// front matter, and title is the safe fallback when neither skill-specific field exists.
    /// </remarks>
    private static string Description(LibraryScopedSkillDocument document) =>
        FirstNonEmpty(
            document.ManualSkillDescription,
            document.ParsedSkillDescription,
            document.Title
        );

    /// <summary>
    /// Selects the first non-empty value from an ordered fallback chain.
    /// </summary>
    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    /// <summary>
    /// Adds Zeeq-specific metadata to prompt list responses.
    /// </summary>
    /// <remarks>
    /// MCP clients do not need this metadata to execute a prompt, but it lets our inspector and
    /// future clients correlate a prompt back to the source document and library.
    /// </remarks>
    private static JsonObject Metadata(LibraryScopedSkillDocument document) =>
        new()
        {
            ["zeeqDocumentId"] = document.DocumentId,
            ["zeeqLibraryId"] = document.LibraryId,
            ["zeeqLibraryName"] = document.LibraryName,
            ["zeeqDocumentPath"] = document.Path,
            ["zeeqScopedSkill"] = LibraryDocumentScopedSkill.Organization.ToString(),
        };

    /// <summary>
    /// Records prompt-list telemetry for both successful and denied discovery attempts.
    /// </summary>
    private static void RecordListTelemetry(
        ClaimsPrincipal? user,
        string? organizationId,
        int promptCount,
        string result
    )
    {
        var userEmail = user.AuthenticatedUser()?.Email ?? "unknown-user";
        var tags = TelemetryTags(userEmail, organizationId, result);
        tags.Add(("prompt_count", promptCount));

        ZeeqTelemetry.SetTags([.. tags]);
        PromptListCounter.Increment(tags: [.. tags]);
    }

    /// <summary>
    /// Records prompt-get telemetry, including resolved document information when available.
    /// </summary>
    private static void RecordGetTelemetry(
        ClaimsPrincipal? user,
        string? organizationId,
        string? promptName,
        LibraryScopedSkillDocument? document,
        string result
    )
    {
        var userEmail = user.AuthenticatedUser()?.Email ?? "unknown-user";
        var tags = TelemetryTags(userEmail, organizationId, result);
        tags.Add(("prompt_name", promptName ?? "unknown-prompt"));

        if (document is not null)
        {
            tags.Add(("document_id", document.DocumentId));
            tags.Add(("document_path", document.Path));
            tags.Add(("library_id", document.LibraryId));
            tags.Add(("library", document.LibraryName));
        }

        ZeeqTelemetry.SetTags([.. tags]);
        PromptGetCounter.Increment(tags: [.. tags]);
    }

    /// <summary>
    /// Records the successful prompt-get usage event consumed by the Prompts dashboard tab.
    /// </summary>
    /// <remarks>
    /// The metrics pipeline promotes <c>organization_id</c>, <c>user</c>, and <c>library</c> into
    /// indexed columns. Prompt/document/client fields remain JSON tags for now, matching the
    /// existing read-path leaderboard approach; promote <c>document_path</c> later only if
    /// top-skill queries become hot enough to require a dedicated index.
    /// </remarks>
    private void RecordPromptUsageTelemetry(
        ClaimsPrincipal? user,
        string organizationId,
        string promptName,
        LibraryScopedSkillDocument document
    )
    {
        var requestTelemetry = McpRequestTelemetryContext.From(httpContextAccessor.HttpContext);
        List<(string Key, object? Value)> tags =
        [
            ("organization_id", organizationId),
            ("user", user.AuthenticatedUser()?.Email ?? "unknown-user"),
            ("library", document.LibraryName),
            ("prompt_name", promptName),
            ("document_id", document.DocumentId),
            ("document_path", document.Path),
            ("library_id", document.LibraryId),
            ("scoped_skill", LibraryDocumentScopedSkill.Organization.ToString()),
        ];

        if (requestTelemetry is null)
        {
            // NOTE: Client dimensions are best-effort. Successful prompt reads should still count
            // even if a future non-HTTP invocation path reaches this service without the MCP
            // message filter's request metadata.
            tags.Add(("user_agent", "unspecified"));
        }
        else
        {
            tags.Add(("user_agent", requestTelemetry.UserAgent));

            if (!string.IsNullOrWhiteSpace(requestTelemetry.ClientName))
            {
                tags.Add(("client_name", requestTelemetry.ClientName));
            }

            if (!string.IsNullOrWhiteSpace(requestTelemetry.ClientVersion))
            {
                tags.Add(("client_version", requestTelemetry.ClientVersion));
            }
        }

        ZeeqTelemetry.SetTags([.. tags]);
        PromptUsageCounter.Increment(tags: [.. tags]);
    }

    /// <summary>
    /// Builds the common telemetry tag set used by dynamic prompt counters and traces.
    /// </summary>
    private static List<(string Key, object? Value)> TelemetryTags(
        string userEmail,
        string? organizationId,
        string result
    )
    {
        List<(string Key, object? Value)> tags = [("user", userEmail), ("result", result)];
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            tags.Add(("organization_id", organizationId));
        }

        return tags;
    }

    /// <summary>
    /// Internal prompt projection with the exposed name, all lookup aliases, and source document.
    /// </summary>
    private sealed record PromptEntry(
        string Name,
        IReadOnlyList<PromptAlias> Aliases,
        LibraryScopedSkillDocument Document
    );

    /// <summary>
    /// One normalized prompt-name alias plus its lookup priority.
    /// </summary>
    private sealed record PromptAlias(int Priority, string Name);
}
