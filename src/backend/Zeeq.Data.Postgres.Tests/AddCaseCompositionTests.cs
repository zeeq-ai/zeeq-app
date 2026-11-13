using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Verifies the complete-draft composition used by the single PR add endpoint.</summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AddCaseCompositionTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private PostgresCodeReviewEvaluationDatasetStore Datasets => new(_context, _clock);

    [Test]
    public async Task Add_PreservesExistingCasesReferencesAndDatasetMetadata()
    {
        var (repository, agent) = await SeedRepositoryAsync();
        var store = Datasets;
        var draft = NewDataset(repository, "composition-key");
        draft.Description = "Keep this description";
        var dataset = await store.CreateAsync(draft, CancellationToken.None);
        var approvedAt = _clock.GetUtcNow();
        var finding = new CodeReviewEvaluationReferenceFinding(
            "ref_1",
            "Title",
            "Summary",
            CodeReviewEvaluationFindingSeverity.Major,
            "a.cs",
            1,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            "usr_1",
            approvedAt
        );
        var reference = new CodeReviewEvaluationReferenceSet(
            agent.Id,
            CodeReviewEvaluationReferenceState.Expected,
            [finding]
        );
        var seeded = await store.SaveAsync(
            NewSaveRequest(
                dataset,
                [NewCase(901, agent.Id, "a", [reference]), NewCase(902, agent.Id, "b", [reference])]
            ),
            CancellationToken.None
        );
        var before = seeded.Cases!.ToDictionary(item => item.PullRequestNumber, item => item.Id);
        var changes = seeded
            .Cases!.Select(item => new CodeReviewEvaluationCaseSave(
                item.Id,
                item.PullRequestNumber,
                null,
                null
            ))
            .ToList();
        changes.Add(NewCase(903, agent.Id, "c"));
        var result = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                2,
                dataset.Name,
                dataset.Description,
                changes
            ),
            CancellationToken.None
        );
        await Assert.That(result.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        _context.ChangeTracker.Clear();
        var persisted = await store.FindAsync(
            dataset.OrganizationId,
            dataset.Id,
            false,
            CancellationToken.None
        );
        var cases = await store.ListCasesAsync(
            dataset.OrganizationId,
            dataset.Id,
            false,
            CancellationToken.None
        );
        await Assert.That(persisted!.Name).IsEqualTo("Fixture dataset");
        await Assert.That(persisted.Description).IsEqualTo("Keep this description");
        foreach (var number in new[] { 901, 902 })
        {
            var item = cases.Single(c => c.PullRequestNumber == number);
            await Assert.That(item.Id).IsEqualTo(before[number]);
            var savedFinding = item.ReferenceSets.Single().Findings.Single();
            await Assert.That(savedFinding.ApprovedByUserId).IsEqualTo("usr_1");
            await Assert.That(savedFinding.ApprovedAtUtc).IsEqualTo(approvedAt);
            await Assert.That(savedFinding.Id).IsEqualTo("ref_1");
        }
        await Assert
            .That(cases.Single(c => c.PullRequestNumber == 903).ReferenceSets.Length)
            .IsEqualTo(0);

        var retained = cases
            .Where(c => c.PullRequestNumber != 901)
            .Select(c => new CodeReviewEvaluationCaseSave(c.Id, c.PullRequestNumber, null, null))
            .ToList();
        var removed = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                3,
                persisted.Name,
                persisted.Description,
                retained
            ),
            CancellationToken.None
        );
        await Assert.That(removed.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        retained.Add(NewCase(901, agent.Id, "recaptured"));
        var readded = await store.SaveAsync(
            new(
                dataset.OrganizationId,
                repository.Id,
                dataset.Id,
                4,
                persisted.Name,
                persisted.Description,
                retained
            ),
            CancellationToken.None
        );
        await Assert.That(readded.Outcome).IsEqualTo(CodeReviewEvaluationDatasetSaveOutcome.Saved);
        _context.ChangeTracker.Clear();
        var restored = (
            await store.ListCasesAsync(
                dataset.OrganizationId,
                dataset.Id,
                false,
                CancellationToken.None
            )
        ).Single(c => c.PullRequestNumber == 901);
        await Assert.That(restored.Id).IsEqualTo(before[901]);
        await Assert.That(restored.ReferenceSets.Length).IsEqualTo(0);
    }

    private async Task<(CodeRepository Repository, CodeReviewerAgent Agent)> SeedRepositoryAsync()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository)
            .BuildAsync();

        var now = _clock.GetUtcNow();
        var agent = new CodeReviewerAgent
        {
            Id = $"cra_{Guid.CreateVersion7():N}",
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            DisplayName = "Fixture reviewer",
            ReviewFacet = "Correctness",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review for correctness.",
            Enabled = true,
            ActivationConfiguration = CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        _context.CodeReviewerAgents.Add(agent);
        await _context.SaveChangesAsync();

        _ = seed;
        return (repository, agent);
    }

    private static CodeReviewEvaluationDataset NewDataset(
        CodeRepository repository,
        string createRequestKey
    ) =>
        new()
        {
            Id = $"cred_{Guid.CreateVersion7():N}",
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            Name = "Fixture dataset",
            Version = 1,
            CreateRequestKey = createRequestKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static CodeReviewEvaluationDatasetSaveRequest NewSaveRequest(
        CodeReviewEvaluationDataset dataset,
        IReadOnlyList<CodeReviewEvaluationCaseSave> cases
    ) =>
        new(
            dataset.OrganizationId,
            dataset.RepositoryId,
            dataset.Id,
            dataset.Version,
            dataset.Name,
            dataset.Description,
            cases
        );

    private static CodeReviewEvaluationCaseSave NewCase(
        int pullRequestNumber,
        string reviewerAgentId,
        string shaSeed,
        IReadOnlyList<CodeReviewEvaluationReferenceSet>? referenceSets = null
    ) => new(null, pullRequestNumber, NewCapture(shaSeed, pullRequestNumber), referenceSets);

    private static CodeReviewEvaluationStagedCapture NewCapture(
        string shaSeed,
        int pullRequestNumber = 901
    ) =>
        new(
            new CodeReviewEvaluationArtifact(
                $"postgres://reviewer-evaluations/org/repo/{shaSeed}/snapshot.json",
                Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(shaSeed)
                    )
                ),
                128
            ),
            pullRequestNumber,
            $"Fixture pull request {shaSeed}",
            "octocat",
            "base-sha",
            $"head-sha-{shaSeed}",
            PullRequestState.Open,
            DateTimeOffset.UtcNow,
            ChangedFileCount: 1,
            OmittedFileCount: 0,
            DeclaredChangedFileCount: 1,
            PullRequestRecordId: null
        );
}
