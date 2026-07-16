using Zeeq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves persisted reviewer configuration into per-run runtime reviewer agents.
/// </summary>
/// <remarks>
/// The resolver intentionally stops before LLM client construction. Persisted
/// reviewer rows store semantic Zeeq model tiers only; the later Agent
/// Framework executor resolves those tiers through organization LLM settings or
/// system defaults immediately before model calls.
/// </remarks>
public sealed partial class CodeReviewerAgentResolver(
    ICodeReviewerAgentStore agents,
    ILogger<CodeReviewerAgentResolver> logger
)
{
    /// <summary>
    /// Built-in reviewer id used when a repository has no enabled saved agents.
    /// </summary>
    /// <remarks>
    /// Aliases the principal-engineer template key so the runtime fallback and
    /// the clonable template catalog share one definition.
    /// </remarks>
    public const string DefaultReviewerId =
        CodeReviewerAgentTemplateLibrary.PrincipalSoftwareEngineerKey;

    /// <summary>
    /// Resolves enabled reviewer agents for the repository and current in-scope files.
    /// </summary>
    /// <remarks>
    /// Resolution mirrors the V1 precedence rule: the fallback reviewer is used
    /// only when there are zero enabled persisted agents. Once a repository has
    /// enabled agents, file activation filters decide the active set; an empty
    /// result is preserved so the runner can emit a no-agents-activated output.
    /// </remarks>
    public async Task<CodeReviewerAgentResolution> ResolveAsync(
        string organizationId,
        string repositoryId,
        IReadOnlyList<CodeReviewFileSnapshot> inScopeFiles,
        CancellationToken cancellationToken
    )
    {
        var configuredAgents = await agents.ListEnabledForRepositoryAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );

        if (configuredAgents.Count == 0)
        {
            LogNoConfiguredAgents(logger, organizationId, repositoryId);
            return new([CreateDefaultRuntimeAgent()], HasConfiguredAgents: false);
        }

        var activeAgents = configuredAgents
            .Where(agent => IsActivated(agent.ActivationConfiguration, inScopeFiles))
            .Select(ToRuntimeAgent)
            .ToArray();

        return new(activeAgents, HasConfiguredAgents: true);
    }

    /// <summary>
    /// Creates the runtime-only fallback reviewer used when a repository has no enabled saved agents.
    /// </summary>
    /// <remarks>
    /// The fallback is deliberately generic and logical. It is not persisted and
    /// is never returned by management APIs, so repositories can move from
    /// fallback to saved agents without seeing phantom configuration in the UI.
    /// Its definition comes from <see cref="CodeReviewerAgentTemplateLibrary.PrincipalSoftwareEngineer" />
    /// so the built-in default and the clonable template stay in sync.
    /// </remarks>
    public static CodeReviewerRuntimeAgent CreateDefaultRuntimeAgent()
    {
        var template = CodeReviewerAgentTemplateLibrary.PrincipalSoftwareEngineer;

        return new(
            template.Key,
            template.DisplayName,
            template.ReviewFacet,
            template.ModelTier,
            template.Prompt,
            template.ActivationConfiguration,
            IsFallbackDefault: true
        );
    }

    private static CodeReviewerRuntimeAgent ToRuntimeAgent(CodeReviewerAgent agent) =>
        new(
            agent.Id,
            agent.DisplayName,
            agent.ReviewFacet,
            agent.ModelTier,
            agent.Prompt,
            agent.ActivationConfiguration
        );

    private static bool IsActivated(
        CodeReviewerActivationConfiguration configuration,
        IReadOnlyList<CodeReviewFileSnapshot> inScopeFiles
    )
    {
        if (inScopeFiles.Count == 0)
        {
            return false;
        }

        return inScopeFiles.Any(file => IsIncluded(file, configuration));
    }

    private static bool IsIncluded(
        CodeReviewFileSnapshot file,
        CodeReviewerActivationConfiguration configuration
    )
    {
        var included =
            configuration.IncludedFiles.Count == 0
            || configuration.IncludedFiles.Any(criteria =>
                CodeReviewFilePatternMatcher.Matches(file, criteria)
            );
        if (!included)
        {
            return false;
        }

        return !configuration.ExcludedFiles.Any(criteria =>
            CodeReviewFilePatternMatcher.Matches(file, criteria)
        );
    }

    [LoggerMessage(
        EventId = 3280,
        Level = LogLevel.Warning,
        Message = "No enabled reviewer agents found for repository; falling back to default reviewer. OrganizationId={OrganizationId}, RepositoryId={RepositoryId}"
    )]
    private static partial void LogNoConfiguredAgents(
        ILogger logger,
        string organizationId,
        string repositoryId
    );
}

/// <summary>
/// Result of resolving reviewer agents for one review run.
/// </summary>
/// <param name="Agents">Runtime agents that should participate in this run.</param>
/// <param name="HasConfiguredAgents">
/// True when enabled persisted agents existed for the repository before file activation was applied.
/// </param>
public sealed record CodeReviewerAgentResolution(
    IReadOnlyList<CodeReviewerRuntimeAgent> Agents,
    bool HasConfiguredAgents
)
{
    /// <summary>
    /// True when saved enabled agents exist, but none activated for the current in-scope files.
    /// </summary>
    public bool NoAgentsActivated => HasConfiguredAgents && Agents.Count == 0;
}
