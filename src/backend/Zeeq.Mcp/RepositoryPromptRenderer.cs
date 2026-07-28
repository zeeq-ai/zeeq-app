using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp;

/// <summary>
/// Renders an organization prompt body, applying the calling repository's placeholder overrides.
/// </summary>
/// <remarks>
/// Organization prompts are tenant-wide documents describing a workflow in general terms. A prompt
/// may declare <c>zeeq_placeholder</c> regions so each repository can inject locally specific rules
/// (language, platform, test runner) without forking the prompt.
///
/// Flow: the MCP client sends <c>x-zeeq-prompts-repo: owner/repo</c> alongside <c>prompts/get</c>.
/// This service resolves that repository <em>inside the authenticated caller's organization</em>,
/// loads the active <see cref="Zeeq.Core.Models.CodeRepositoryPromptConfiguration" /> for the
/// requested document, and substitutes. Any failure to resolve — no header, unparseable header,
/// unknown repository, inactive or unconfigured prompt — degrades to rendering the authored defaults
/// rather than erroring, because a client-supplied header must never be able to fail a prompt fetch.
///
/// The organization scope is the tenant boundary and is taken from the authenticated principal, not
/// from the header. A repository name that exists in another tenant resolves to nothing here.
/// </remarks>
internal sealed partial class RepositoryPromptRenderer(
    ICodeRepositoryStore repositories,
    ICodeRepositoryPromptConfigurationStore promptConfigurations,
    IHttpContextAccessor httpContextAccessor,
    HybridCache cache,
    ILogger<RepositoryPromptRenderer> logger
)
{
    /// <summary>
    /// Header naming the repository whose placeholder values should be applied.
    /// </summary>
    /// <remarks>
    /// Value shape is the provider-qualified name, for example <c>zeeq-ai/zeeq-app</c>.
    /// </remarks>
    internal const string RepositoryHeaderName = "x-zeeq-prompts-repo";

    /// <summary>
    /// Only GitHub repositories are addressable today; the provider is fixed until a second
    /// provider exists and the header needs a qualifier.
    /// </summary>
    private const string Provider = "github";

    /// <summary>
    /// Guards against an absurd header value before it reaches a database query.
    /// </summary>
    private const int MaximumRepositoryNameLength = 512;

    /// <summary>
    /// Rendered bodies are cheap to recompute and expensive to move, so they never leave this
    /// process. A distributed round trip would cost more than the substitution it avoids.
    /// </summary>
    private static readonly HybridCacheEntryOptions RenderCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(30),
        LocalCacheExpiration = TimeSpan.FromMinutes(30),
        Flags = HybridCacheEntryFlags.DisableDistributedCache,
    };

    /// <summary>
    /// Produces the prompt text to return to the MCP client.
    /// </summary>
    /// <remarks>
    /// Ordering is deliberate. The placeholder-marker check runs first so a plain prompt — expected
    /// to be the common case — costs one vectorized scan and issues no repository or configuration
    /// query at all. Only a document that actually declares placeholders pays for resolution.
    ///
    /// Substitution itself always runs for a templated document, including when no overrides apply,
    /// so raw <c>zeeq_placeholder</c> markup can never reach a client.
    /// </remarks>
    /// <param name="document">Resolved prompt document, including its body and version stamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered prompt body.</returns>
    public async ValueTask<string> RenderAsync(
        LibraryScopedSkillDocument document,
        CancellationToken cancellationToken
    )
    {
        var content = document.Content ?? string.Empty;

        // Gate everything else: no placeholders means no lookups and no allocation.
        if (!PromptPlaceholderParser.ContainsPlaceholders(content))
        {
            return content;
        }

        var repositoryName = ReadRepositoryHeader();
        if (repositoryName is null)
        {
            return await RenderAsync(
                document,
                content,
                repository: null,
                configuration: null,
                cancellationToken
            );
        }

        // Paused repositories still resolve here: Enabled gates webhook review work, and pausing
        // reviews says nothing about how an agent's prompts should render.
        var repository = await repositories.FindConfiguredForOrganizationByProviderIdentityAsync(
            document.OrganizationId,
            Provider,
            repositoryName,
            cancellationToken
        );
        if (repository is null)
        {
            LogRepositoryNotResolved(logger, repositoryName, document.OrganizationId);

            return await RenderAsync(
                document,
                content,
                repository: null,
                configuration: null,
                cancellationToken
            );
        }

        var configuration = await promptConfigurations.FindActiveForPromptAsync(
            document.OrganizationId,
            repository.Id,
            document.LibraryId,
            document.DocumentId,
            cancellationToken
        );

        ZeeqTelemetry.SetTags(
            ("prompt.repository", repository.OwnerQualifiedName),
            ("prompt.repository_id", repository.Id),
            ("prompt.overrides_applied", configuration is { PlaceholderValues.Count: > 0 })
        );

        return await RenderAsync(
            document,
            content,
            repository.Id,
            configuration,
            cancellationToken
        );
    }

    /// <summary>
    /// Renders through the per-(document, repository) cache.
    /// </summary>
    /// <remarks>
    /// The key carries the full prompt identity, resolved repository identity, and persisted config
    /// timestamp, so a document edit or configuration save mints a new key and stale entries are
    /// simply never read again. That removes the need for eviction hooks on the document-write and
    /// configuration-write paths, which are exactly the couplings that rot.
    /// </remarks>
    private ValueTask<string> RenderAsync(
        LibraryScopedSkillDocument document,
        string content,
        string? repository,
        Zeeq.Core.Models.CodeRepositoryPromptConfiguration? configuration,
        CancellationToken cancellationToken
    )
    {
        var overrides = configuration?.PlaceholderValues;
        var hasOverrides = overrides is { Count: > 0 };
        var repositoryKey = repository ?? "-";
        // NOTE: Active configurations with zero values still participate in the render decision, so
        // their version must invalidate the cache when a user toggles or clears prompt overrides.
        var configurationVersion = configuration?.UpdatedAtUtc.UtcTicks ?? 0;
        var key =
            $"prompt-render:{document.OrganizationId}:{document.LibraryId}:{document.DocumentId}:{document.UpdatedAt.UtcTicks}:{repositoryKey}:{configurationVersion}";

        return cache.GetOrCreateAsync(
            key,
            (content, overrides: hasOverrides ? overrides : null),
            static (state, _) =>
                ValueTask.FromResult(
                    PromptPlaceholderParser.Substitute(state.content, state.overrides)
                ),
            RenderCacheOptions,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Reads and validates the repository header for the current request.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null" /> for anything unusable rather than throwing. The value is
    /// client-supplied and purely additive: a typo should quietly fall back to authored defaults, not
    /// break the caller's prompt fetch.
    ///
    /// Validation is shape-only (a single <c>owner/name</c> separator, both halves non-empty). Whether
    /// the repository actually exists is decided by the organization-scoped lookup, which is also the
    /// tenant boundary.
    /// </remarks>
    /// <returns>The trimmed <c>owner/repo</c> value, or <see langword="null" /> when absent or malformed.</returns>
    private string? ReadRepositoryHeader()
    {
        var raw = httpContextAccessor
            .HttpContext?.Request.Headers[RepositoryHeaderName]
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length > MaximumRepositoryNameLength)
        {
            LogRepositoryHeaderRejected(logger, value.Length);

            return null;
        }

        var separator = value.IndexOf('/');
        var isWellFormed =
            separator > 0 && separator < value.Length - 1 && value.IndexOf('/', separator + 1) < 0;

        if (!isWellFormed)
        {
            LogRepositoryHeaderMalformed(logger, value);

            return null;
        }

        return value;
    }

    [LoggerMessage(
        EventId = 3410,
        Level = LogLevel.Debug,
        Message = "MCP prompt repository header did not resolve to a configured repository. Repository={RepositoryName}, OrganizationId={OrganizationId}"
    )]
    private static partial void LogRepositoryNotResolved(
        ILogger logger,
        string repositoryName,
        string organizationId
    );

    [LoggerMessage(
        EventId = 3411,
        Level = LogLevel.Debug,
        Message = "MCP prompt repository header was malformed and ignored. Value={HeaderValue}"
    )]
    private static partial void LogRepositoryHeaderMalformed(ILogger logger, string headerValue);

    [LoggerMessage(
        EventId = 3412,
        Level = LogLevel.Debug,
        Message = "MCP prompt repository header exceeded the maximum length and was ignored. Length={HeaderLength}"
    )]
    private static partial void LogRepositoryHeaderRejected(ILogger logger, int headerLength);
}
