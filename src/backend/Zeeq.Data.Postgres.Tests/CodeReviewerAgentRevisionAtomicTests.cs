using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Proves configuration and revision history share one commit, including failed inserts.
/// Run with --treenode-filter "/*/*/CodeReviewerAgentRevisionAtomicTests/*".
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CodeReviewerAgentRevisionAtomicTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task Update_RevisionInsertFailure_RollsBackConfigurationAndHistory()
    {
        var agent = await SeedAsync();
        var store = new PostgresCodeReviewerAgentStore(_context);
        var replacement = Replacement(agent, "Changed prompt");
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.SaveWithRevisionAsync(
                replacement,
                false,
                "usr_test",
                new string('x', 201),
                CancellationToken.None
            )
        );

        // SaveChanges rolls its failed batch back to its savepoint. Clear pending EF entries
        // so this assertion reads persisted state instead of the failed replacement object.
        _context.ChangeTracker.Clear();
        var saved = await _context
            .CodeReviewerAgents.AsNoTracking()
            .SingleAsync(x => x.Id == agent.Id);
        await Assert.That(saved.Prompt).IsEqualTo("Original prompt");
        await Assert
            .That(await _context.CodeReviewerAgentRevisions.CountAsync(x => x.AgentId == agent.Id))
            .IsEqualTo(0);
    }

    [Test]
    public async Task Update_CapturesLegacyPredecessorAndNewContent_WithoutNoOpDuplicates()
    {
        var agent = await SeedAsync();
        var store = new PostgresCodeReviewerAgentStore(_context);
        var replacement = Replacement(agent, "Changed prompt");
        await store.SaveWithRevisionAsync(
            replacement,
            false,
            "usr_test",
            "candidate",
            CancellationToken.None
        );
        await store.SaveWithRevisionAsync(
            replacement,
            false,
            "usr_test",
            "same content",
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();
        var revisions = await _context
            .CodeReviewerAgentRevisions.AsNoTracking()
            .Where(x => x.AgentId == agent.Id)
            .ToListAsync();
        await Assert.That(revisions.Count).IsEqualTo(2);
        await Assert.That(revisions.Any(x => x.Configuration.Prompt == "Original prompt")).IsTrue();
        await Assert
            .That(
                revisions.Any(x =>
                    x.Configuration.Prompt == "Changed prompt" && x.Label == "candidate"
                )
            )
            .IsTrue();
        var saved = await _context
            .CodeReviewerAgents.AsNoTracking()
            .SingleAsync(x => x.Id == agent.Id);
        await Assert.That(saved.Prompt).IsEqualTo("Changed prompt");
    }

    [Test]
    public async Task Create_RevisionInsertFailure_DoesNotLeaveAgent()
    {
        var legacy = await SeedAsync();
        var created = Replacement(legacy, "New agent", $"cra_{Guid.CreateVersion7():N}");
        var store = new PostgresCodeReviewerAgentStore(_context);
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.SaveWithRevisionAsync(
                created,
                true,
                "usr_test",
                new string('x', 201),
                CancellationToken.None
            )
        );
        _context.ChangeTracker.Clear();
        await Assert
            .That(await _context.CodeReviewerAgents.AnyAsync(x => x.Id == created.Id))
            .IsFalse();
        await Assert
            .That(await _context.CodeReviewerAgentRevisions.AnyAsync(x => x.AgentId == created.Id))
            .IsFalse();
    }

    private async Task<CodeReviewerAgent> SeedAsync()
    {
        await TestBiome.Start(_context, out var _).AddRepository(out var repository).BuildAsync();
        var agent = new CodeReviewerAgent
        {
            Id = $"cra_{Guid.CreateVersion7():N}",
            OrganizationId = repository.OrganizationId,
            RepositoryId = repository.Id,
            TeamId = repository.TeamId,
            DisplayName = "Reviewer",
            ReviewFacet = "Correctness",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Original prompt",
            Enabled = true,
            ActivationConfiguration = new(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _context.CodeReviewerAgents.Add(agent);
        await _context.SaveChangesAsync();
        return agent;
    }

    private static CodeReviewerAgent Replacement(
        CodeReviewerAgent agent,
        string prompt,
        string? id = null
    ) =>
        new()
        {
            Id = id ?? agent.Id,
            OrganizationId = agent.OrganizationId,
            RepositoryId = agent.RepositoryId,
            TeamId = agent.TeamId,
            DisplayName = agent.DisplayName,
            ReviewFacet = agent.ReviewFacet,
            ModelTier = agent.ModelTier,
            Prompt = prompt,
            Enabled = agent.Enabled,
            ActivationConfiguration = new(),
            CreatedAtUtc = agent.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
}
