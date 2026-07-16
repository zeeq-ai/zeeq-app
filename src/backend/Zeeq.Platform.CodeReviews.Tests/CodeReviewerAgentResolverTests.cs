using Zeeq.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests runtime reviewer-agent resolution.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewerAgentResolverTests/*"
/// </summary>
public sealed class CodeReviewerAgentResolverTests
{
    [Test]
    public async Task ResolveAsync_WithNoEnabledAgents_ReturnsBuiltInDefaultReviewer()
    {
        var store = new TestCodeReviewerAgentStore();
        var resolver = new CodeReviewerAgentResolver(
            store,
            NullLogger<CodeReviewerAgentResolver>.Instance
        );

        var resolution = await resolver.ResolveAsync(
            "org_123",
            "repo_123",
            Files(),
            CancellationToken.None
        );

        var agent = resolution.Agents.Single();

        await Assert.That(resolution.HasConfiguredAgents).IsFalse();
        await Assert.That(resolution.NoAgentsActivated).IsFalse();
        await Assert.That(agent.Id).IsEqualTo(CodeReviewerAgentResolver.DefaultReviewerId);
        await Assert.That(agent.DisplayName).IsEqualTo("Principal Software Engineer");
        await Assert.That(agent.ReviewFacet).IsEqualTo("General");
        await Assert.That(agent.ModelTier).IsEqualTo(CodeReviewModelTier.Fast);
        await Assert.That(agent.IsFallbackDefault).IsTrue();
        await Assert.That(agent.Prompt).Contains("<evaluation_criteria>");
    }

    [Test]
    public async Task ResolveAsync_WithEnabledAgents_DropsDefaultAndReturnsActivatedAgents()
    {
        var store = new TestCodeReviewerAgentStore
        {
            Agents =
            [
                Agent(
                    "agent_backend",
                    "Backend",
                    activation: new()
                    {
                        IncludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.Extension,
                                Pattern = ".cs",
                            },
                        ],
                    }
                ),
                Agent(
                    "agent_docs",
                    "Docs",
                    activation: new()
                    {
                        IncludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.Extension,
                                Pattern = ".md",
                            },
                        ],
                    }
                ),
            ],
        };
        var resolver = new CodeReviewerAgentResolver(
            store,
            NullLogger<CodeReviewerAgentResolver>.Instance
        );

        var resolution = await resolver.ResolveAsync(
            "org_123",
            "repo_123",
            [Files()[0]],
            CancellationToken.None
        );

        await Assert.That(resolution.HasConfiguredAgents).IsTrue();
        await Assert.That(resolution.NoAgentsActivated).IsFalse();
        await Assert
            .That(resolution.Agents.Select(agent => agent.Id))
            .IsEquivalentTo(["agent_backend"]);
        await Assert.That(resolution.Agents.Single().IsFallbackDefault).IsFalse();
    }

    [Test]
    public async Task ResolveAsync_WithConfiguredAgentsButNoActivation_ReturnsNoAgentsActivated()
    {
        var store = new TestCodeReviewerAgentStore
        {
            Agents =
            [
                Agent(
                    "agent_backend",
                    "Backend",
                    activation: new()
                    {
                        IncludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.Extension,
                                Pattern = ".cs",
                            },
                        ],
                        ExcludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.ExactPath,
                                Pattern = "src/backend/Code.cs",
                            },
                        ],
                    }
                ),
            ],
        };
        var resolver = new CodeReviewerAgentResolver(
            store,
            NullLogger<CodeReviewerAgentResolver>.Instance
        );

        var resolution = await resolver.ResolveAsync(
            "org_123",
            "repo_123",
            [Files()[0]],
            CancellationToken.None
        );

        await Assert.That(resolution.HasConfiguredAgents).IsTrue();
        await Assert.That(resolution.NoAgentsActivated).IsTrue();
        await Assert.That(resolution.Agents).IsEmpty();
    }

    [Test]
    public async Task ResolveAsync_WithOnlyDisabledAgents_ReturnsBuiltInDefaultReviewer()
    {
        var store = new TestCodeReviewerAgentStore
        {
            Agents = [Agent("agent_disabled", "Disabled", enabled: false)],
        };
        var resolver = new CodeReviewerAgentResolver(
            store,
            NullLogger<CodeReviewerAgentResolver>.Instance
        );

        var resolution = await resolver.ResolveAsync(
            "org_123",
            "repo_123",
            Files(),
            CancellationToken.None
        );

        await Assert.That(resolution.HasConfiguredAgents).IsFalse();
        await Assert.That(resolution.Agents.Single().IsFallbackDefault).IsTrue();
    }

    private static CodeReviewerAgent Agent(
        string id,
        string facet,
        bool enabled = true,
        CodeReviewerActivationConfiguration? activation = null
    ) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            DisplayName = facet + " Reviewer",
            ReviewFacet = facet,
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review " + facet + " concerns.",
            Enabled = enabled,
            ActivationConfiguration = activation ?? CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static IReadOnlyList<CodeReviewFileSnapshot> Files() =>
        [
            new(
                "src/backend/Code.cs",
                null,
                CodeReviewFileMutationState.Modified,
                "@@ -1 +1\n+var value = 1;"
            ),
            new("docs/readme.md", null, CodeReviewFileMutationState.Added, "@@ -0 +1\n+# Docs"),
        ];

    private sealed class TestCodeReviewerAgentStore : ICodeReviewerAgentStore
    {
        public IReadOnlyList<CodeReviewerAgent> Agents { get; init; } = [];

        public Task<IReadOnlyList<CodeReviewerAgent>> ListForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([
                .. Agents.Where(agent =>
                    agent.OrganizationId == organizationId && agent.RepositoryId == repositoryId
                ),
            ]);

        public Task<IReadOnlyList<CodeReviewerAgent>> ListEnabledForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([
                .. Agents.Where(agent =>
                    agent.OrganizationId == organizationId
                    && agent.RepositoryId == repositoryId
                    && agent.Enabled
                ),
            ]);

        public Task<CodeReviewerAgent?> FindAsync(
            string organizationId,
            string agentId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Agents.FirstOrDefault(agent =>
                    agent.OrganizationId == organizationId && agent.Id == agentId
                )
            );

        public Task<CodeReviewerAgent> AddAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        ) => Task.FromResult(agent);

        public Task<CodeReviewerAgent> UpdateAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        ) => Task.FromResult(agent);

        public Task<bool> DisableAsync(
            string organizationId,
            string agentId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);
    }
}
