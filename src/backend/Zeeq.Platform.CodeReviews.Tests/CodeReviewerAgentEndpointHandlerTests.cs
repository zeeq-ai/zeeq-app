using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for repository reviewer-agent endpoints.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewerAgentEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewerAgentEndpointHandlerTests
{
    [Test]
    public async Task ListRepositoryCodeReviewerAgents_WithAdmin_ReturnsRepositoryAgents()
    {
        var fixture = Fixture.Create(role: "admin");
        fixture.Agents.Agents.Add(Agent(displayName: "Security Reviewer"));

        var result = await fixture.ListHandler.HandleAsync(
            "org_123",
            "repo_123",
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewerAgentListResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items).Count().IsEqualTo(1);
        await Assert.That(ok.Value.Items.Single().DisplayName).IsEqualTo("Security Reviewer");
    }

    [Test]
    public async Task ListRepositoryCodeReviewerAgents_WithMember_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.ListHandler.HandleAsync(
            "org_123",
            "repo_123",
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
    }

    [Test]
    public async Task CreateRepositoryCodeReviewerAgent_WithAdmin_CreatesRepositoryScopedAgent()
    {
        var fixture = Fixture.Create(role: "owner");

        var result = await fixture.CreateHandler.HandleAsync(
            "org_123",
            "repo_123",
            CreateRequest(
                displayName: " Security Reviewer ",
                reviewFacet: " Security ",
                prompt: " Review authentication and authorization. ",
                includePattern: " src/backend/ "
            ),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewerAgentResponse>;
        var created = fixture.Agents.Agents.Single();

        await Assert.That(ok).IsNotNull();
        await Assert.That(created.Id).StartsWith("cra_");
        await Assert.That(created.OrganizationId).IsEqualTo("org_123");
        await Assert.That(created.TeamId).IsEqualTo("team_123");
        await Assert.That(created.RepositoryId).IsEqualTo("repo_123");
        await Assert.That(created.DisplayName).IsEqualTo("Security Reviewer");
        await Assert.That(created.ReviewFacet).IsEqualTo("Security");
        await Assert.That(created.Prompt).IsEqualTo("Review authentication and authorization.");
        await Assert
            .That(created.ActivationConfiguration.IncludedFiles.Single().Pattern)
            .IsEqualTo("src/backend/");
        await Assert.That(ok!.Value!.Agent.ModelTier).IsEqualTo(CodeReviewModelTier.High);
    }

    [Test]
    public async Task CreateRepositoryCodeReviewerAgent_WithInvalidTier_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");

        var result = await fixture.CreateHandler.HandleAsync(
            "org_123",
            "repo_123",
            CreateRequest(modelTier: (CodeReviewModelTier)999),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_agent");
        await Assert.That(fixture.Agents.Agents).IsEmpty();
    }

    [Test]
    public async Task CreateRepositoryCodeReviewerAgent_WhenRepositoryAlreadyHasTenAgents_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");
        for (var index = 0; index < 10; index++)
        {
            fixture.Agents.Agents.Add(
                Agent(id: $"agent_{index}", displayName: $"Reviewer {index}")
            );
        }

        var result = await fixture.CreateHandler.HandleAsync(
            "org_123",
            "repo_123",
            CreateRequest(displayName: "Reviewer 11"),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("agent_limit_reached");
        await Assert.That(fixture.Agents.Agents).Count().IsEqualTo(10);
    }

    [Test]
    public async Task UpdateRepositoryCodeReviewerAgent_WithAdmin_UpdatesEditableFields()
    {
        var fixture = Fixture.Create(role: "admin");
        var existing = Agent(displayName: "Security Reviewer");
        fixture.Agents.Agents.Add(existing);

        var result = await fixture.UpdateHandler.HandleAsync(
            "org_123",
            "repo_123",
            existing.Id,
            UpdateRequest(
                displayName: "Performance Reviewer",
                reviewFacet: "Performance",
                modelTier: CodeReviewModelTier.Max,
                prompt: "Review hot paths.",
                enabled: false,
                excludePattern: "*.generated.cs"
            ),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewerAgentResponse>;
        var updated = fixture.Agents.Agents.Single();

        await Assert.That(ok).IsNotNull();
        await Assert.That(updated.Id).IsEqualTo(existing.Id);
        await Assert.That(updated.CreatedAtUtc).IsEqualTo(existing.CreatedAtUtc);
        await Assert.That(updated.DisplayName).IsEqualTo("Performance Reviewer");
        await Assert.That(updated.ReviewFacet).IsEqualTo("Performance");
        await Assert.That(updated.ModelTier).IsEqualTo(CodeReviewModelTier.Max);
        await Assert.That(updated.Enabled).IsFalse();
        await Assert
            .That(updated.ActivationConfiguration.ExcludedFiles.Single().Pattern)
            .IsEqualTo("*.generated.cs");
        await Assert.That(ok!.Value!.Agent.Enabled).IsFalse();
    }

    [Test]
    public async Task UpdateRepositoryCodeReviewerAgent_WithDifferentRepositoryAgent_ReturnsNotFound()
    {
        var fixture = Fixture.Create(role: "admin");
        var existing = Agent(repositoryId: "repo_other");
        fixture.Agents.Agents.Add(existing);

        var result = await fixture.UpdateHandler.HandleAsync(
            "org_123",
            "repo_123",
            existing.Id,
            UpdateRequest(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(fixture.Agents.UpdateCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteRepositoryCodeReviewerAgent_WithAdmin_DisablesAgent()
    {
        var fixture = Fixture.Create(role: "owner");
        var existing = Agent();
        fixture.Agents.Agents.Add(existing);

        var result = await fixture.DeleteHandler.HandleAsync(
            "org_123",
            "repo_123",
            existing.Id,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        await Assert.That(fixture.Agents.DisableCalls).IsEqualTo(1);
        await Assert.That(existing.Enabled).IsFalse();
        await Assert.That(existing.DisabledAtUtc).IsNotNull();
    }

    private static CreateCodeReviewerAgentRequest CreateRequest(
        string displayName = "Security Reviewer",
        string reviewFacet = "Security",
        CodeReviewModelTier modelTier = CodeReviewModelTier.High,
        string prompt = "Review for security issues.",
        bool enabled = true,
        string includePattern = "src/backend/"
    ) =>
        new(
            displayName,
            reviewFacet,
            modelTier,
            prompt,
            enabled,
            new CodeReviewerActivationConfigurationDto(
                [
                    new CodeReviewFileMatchCriteriaDto(
                        CodeReviewFileNameMatchType.PathPrefix,
                        includePattern
                    ),
                ],
                []
            )
        );

    private static UpdateCodeReviewerAgentRequest UpdateRequest(
        string displayName = "Security Reviewer",
        string reviewFacet = "Security",
        CodeReviewModelTier modelTier = CodeReviewModelTier.High,
        string prompt = "Review for security issues.",
        bool enabled = true,
        string excludePattern = "bin/"
    ) =>
        new(
            displayName,
            reviewFacet,
            modelTier,
            prompt,
            enabled,
            new CodeReviewerActivationConfigurationDto(
                [],
                [
                    new CodeReviewFileMatchCriteriaDto(
                        CodeReviewFileNameMatchType.Glob,
                        excludePattern
                    ),
                ]
            )
        );

    private static CodeReviewerAgent Agent(
        string id = "agent_123",
        string organizationId = "org_123",
        string repositoryId = "repo_123",
        string displayName = "General Reviewer"
    )
    {
        var createdAtUtc = DateTimeOffset.UtcNow.AddDays(-1);

        return new()
        {
            Id = id,
            OrganizationId = organizationId,
            TeamId = "team_123",
            RepositoryId = repositoryId,
            DisplayName = displayName,
            ReviewFacet = "General",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review the pull request.",
            Enabled = true,
            ActivationConfiguration = CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, "usr_123")],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        private Fixture() { }

        public TestCodeRepositoryStore Repositories { get; } = new();

        public TestCodeReviewerAgentStore Agents { get; } = new();

        public ListRepositoryCodeReviewerAgentsHandler ListHandler { get; private set; } = null!;

        public CreateRepositoryCodeReviewerAgentHandler CreateHandler { get; private set; } = null!;

        public UpdateRepositoryCodeReviewerAgentHandler UpdateHandler { get; private set; } = null!;

        public DeleteRepositoryCodeReviewerAgentHandler DeleteHandler { get; private set; } = null!;

        public static Fixture Create(string role)
        {
            var fixture = new Fixture();
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "mem_123",
                            OrganizationId = "org_123",
                            UserId = "usr_123",
                            Role = role,
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );
            var authorization = new CodeReviewAuthorization(memberships);

            fixture.ListHandler = new(authorization, fixture.Repositories, fixture.Agents);
            fixture.CreateHandler = new(authorization, fixture.Repositories, fixture.Agents);
            fixture.UpdateHandler = new(authorization, fixture.Repositories, fixture.Agents);
            fixture.DeleteHandler = new(authorization, fixture.Repositories, fixture.Agents);

            return fixture;
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public CodeRepository Repository { get; set; } =
            new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                Provider = "github",
                OwnerQualifiedName = "zeeq-ai/zeeq",
                DisplayName = "zeeq-ai/zeeq",
                Enabled = true,
                ReviewConfiguration = CodeRepositoryReviewConfiguration.Empty,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Provider lookup is not used by these tests.");

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository listing is not used by these tests.");

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository listing is not used by these tests.");

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repository.OrganizationId == organizationId && Repository.Id == repositoryId
                    ? Repository
                    : null
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository saving is not used by these tests.");

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository disabling is not used by these tests.");
    }

    private sealed class TestCodeReviewerAgentStore : ICodeReviewerAgentStore
    {
        public List<CodeReviewerAgent> Agents { get; } = [];

        public int UpdateCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public Task<IReadOnlyList<CodeReviewerAgent>> ListForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeReviewerAgent>>(
                Agents
                    .Where(agent =>
                        agent.OrganizationId == organizationId
                        && agent.RepositoryId == repositoryId
                        && agent.DisabledAtUtc is null
                    )
                    .OrderBy(agent => agent.DisplayName)
                    .ThenBy(agent => agent.Id)
                    .ToArray()
            );

        public Task<IReadOnlyList<CodeReviewerAgent>> ListEnabledForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Enabled agent listing is not used by these tests.");

        public Task<CodeReviewerAgent?> FindAsync(
            string organizationId,
            string agentId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Agents.FirstOrDefault(agent =>
                    agent.OrganizationId == organizationId
                    && agent.Id == agentId
                    && agent.DisabledAtUtc is null
                )
            );

        public Task<CodeReviewerAgent> AddAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        )
        {
            Agents.Add(agent);

            return Task.FromResult(agent);
        }

        public Task<CodeReviewerAgent> UpdateAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        )
        {
            UpdateCalls++;

            return Task.FromResult(agent);
        }

        public async Task<bool> DisableAsync(
            string organizationId,
            string agentId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        )
        {
            DisableCalls++;
            var agent = await FindAsync(organizationId, agentId, cancellationToken);
            if (agent is null)
            {
                return false;
            }

            agent.Enabled = false;
            agent.DisabledAtUtc = disabledAtUtc;
            agent.UpdatedAtUtc = disabledAtUtc;

            return true;
        }
    }
}
