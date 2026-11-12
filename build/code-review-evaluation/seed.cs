#:property TargetFramework=net11.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:project ../../src/backend/Zeeq.Data.Postgres/Zeeq.Data.Postgres.csproj

// Reviewer backtesting fixture helper.
//
// Development-only, explicit, repeatable. It never runs at application startup.
//
//   BT_RUN_ID=local1 DOTNET_ENVIRONMENT=Development dotnet run build/code-review-evaluation/seed.cs
//   BT_RUN_ID=local1 BT_MODE=cleanup DOTNET_ENVIRONMENT=Development dotnet run build/code-review-evaluation/seed.cs
//
// Run it from the repository root. Every row and artifact it creates is namespaced bt_{runId}
// and recorded in build/code-review-evaluation/bt_{runId}.manifest.json. Cleanup deletes
// exactly what the manifest owns, in dependency order, and refuses to run without a manifest.
// The helper never touches a normal user's repositories, identities, permissions, tier
// mappings or artifacts.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres;
using Zeeq.Platform.CodeReviews;

const string AlphaOrganizationId = "org_0factor_e2e_alpha";
const string BetaOrganizationId = "org_0factor_e2e_beta";

var runId = Environment.GetEnvironmentVariable("BT_RUN_ID");
if (string.IsNullOrWhiteSpace(runId) || !runId.All(char.IsLetterOrDigit))
{
    throw new InvalidOperationException(
        "BT_RUN_ID is required and must be alphanumeric, for example BT_RUN_ID=local1."
    );
}

if (
    !string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        "Development",
        StringComparison.OrdinalIgnoreCase
    )
)
{
    throw new InvalidOperationException(
        "The evaluation fixture can only run with DOTNET_ENVIRONMENT=Development."
    );
}

var rawConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__zeeq")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__zeeq-db")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__zeeq or ConnectionStrings__zeeq-db must be supplied by the local Aspire resource."
    );

var connection = new NpgsqlConnectionStringBuilder(rawConnectionString);
if (
    !IsLoopback(connection.Host ?? string.Empty)
    || !string.Equals(connection.Database, "zeeq", StringComparison.OrdinalIgnoreCase)
)
{
    throw new InvalidOperationException(
        "The evaluation fixture can only connect to the local zeeq development database."
    );
}

var namespacePrefix = $"bt_{runId}";
var manifestPath = Path.GetFullPath(
    Path.Combine(
        Directory.GetCurrentDirectory(),
        "build",
        "code-review-evaluation",
        $"{namespacePrefix}.manifest.json"
    )
);

var options = new DbContextOptionsBuilder<PostgresDbContext>()
    .UseNpgsql(connection.ConnectionString, npgsql =>
    {
        npgsql.SetPostgresVersion(18, 0);
        npgsql.UseVector();
    })
    .UseOpenIddict()
    .UseSnakeCaseNamingConvention()
    .Options;

await using var db = new PostgresDbContext(options);
var now = DateTimeOffset.UtcNow;

if (
    string.Equals(
        Environment.GetEnvironmentVariable("BT_MODE"),
        "cleanup",
        StringComparison.OrdinalIgnoreCase
    )
)
{
    return await CleanupAsync();
}

return await SeedAsync();

async Task<int> SeedAsync()
{
    if (File.Exists(manifestPath))
    {
        throw new InvalidOperationException(
            $"Fixture manifest already exists for {namespacePrefix}. Run cleanup first to avoid mixing runs."
        );
    }

    foreach (var organizationId in new[] { AlphaOrganizationId, BetaOrganizationId })
    {
        var exists = await db
            .Organizations.AsNoTracking()
            .AnyAsync(organization => organization.Id == organizationId);
        if (!exists)
        {
            throw new InvalidOperationException(
                $"Organization {organizationId} is missing. Seed the 0factor baseline first."
            );
        }
    }

    await db.Database.BeginTransactionAsync();

    var alpha = await SeedRepositoryAsync(AlphaOrganizationId, "alpha", includeDataset: true);
    var beta = await SeedRepositoryAsync(BetaOrganizationId, "beta", includeDataset: false);

    await db.SaveChangesAsync();
    await db.Database.CommitTransactionAsync();

    var manifest = new FixtureManifest(
        namespacePrefix,
        now,
        [alpha.RepositoryId, beta.RepositoryId],
        [.. alpha.AgentIds, .. beta.AgentIds],
        [.. alpha.RevisionIds, .. beta.RevisionIds],
        alpha.DatasetIds,
        alpha.CaseIds,
        alpha.ArtifactUris
    );

    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    await File.WriteAllTextAsync(
        manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })
    );

    Console.WriteLine($"Fixture seeded: {namespacePrefix}");
    Console.WriteLine($"  alpha repository: {alpha.RepositoryId}");
    Console.WriteLine($"  beta repository:  {beta.RepositoryId}");
    Console.WriteLine($"  dataset:          {alpha.DatasetIds[0]}");
    Console.WriteLine($"  capture dataset:  {alpha.DatasetIds[1]}");
    Console.WriteLine($"  manifest:         {manifestPath}");

    return 0;
}

async Task<RepositoryFixture> SeedRepositoryAsync(
    string organizationId,
    string slug,
    bool includeDataset
)
{
    var repositoryId = $"repo_{namespacePrefix}_{slug}";
    db.CodeRepositories.Add(
        new CodeRepository
        {
            Id = repositoryId,
            OrganizationId = organizationId,
            Provider = "github",
            OwnerQualifiedName = $"zeeq-fixture/{namespacePrefix}-{slug}",
            DisplayName = $"{namespacePrefix} {slug}",
            Enabled = true,
            VisibleInLibraryPicker = false,
            LibraryIds = [],
            ReviewConfiguration = new CodeRepositoryReviewConfiguration
            {
                FileFilter = new CodeReviewFileFilter
                {
                    IncludedFiles = [],
                    // P4 must produce NoFilesInScope: documentation changes are out of review
                    // scope for this fixture repository.
                    ExcludedFiles =
                    [
                        new()
                        {
                            MatchType = CodeReviewFileNameMatchType.PathPrefix,
                            Pattern = "docs/",
                        },
                    ],
                },
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }
    );

    // Pull request rows make the fixture pull requests browsable in Add PRs. They are owned by
    // the fixture repository and cleaned up with it; no production review history is created.
    for (var number = 901; number <= 910; number++)
    {
        var createdAt = now.AddSeconds(number);
        var prId = $"pr_{namespacePrefix}_{slug}_{number}";
        var title = FixtureCases
            .Definitions.FirstOrDefault(definition => definition.Number == number)
            ?.Title
            ?? $"Fixture pull request {number}";

        db.PullRequestRecords.Add(
            new PullRequestRecord
            {
                Id = prId,
                OrganizationId = organizationId,
                RepositoryId = repositoryId,
                OwnerQualifiedRepoName = $"zeeq-fixture/{namespacePrefix}-{slug}",
                PullRequestNumber = number,
                GitHubNodeId = $"PR_{namespacePrefix}_{number}",
                Title = title,
                Branch = $"fixture/{number}",
                BaseBranch = "main",
                HeadSha = $"head-{number}",
                AuthorLogin = "e2e-alice-owner",
                HtmlUrl = $"https://github.com/zeeq-fixture/{namespacePrefix}-{slug}/pull/{number}",
                IsDraft = false,
                State = PullRequestState.Open,
                ClaimStatus = PullRequestClaimStatus.Unclaimed,
                CreatedAtUtc = createdAt,
                CreatedFromWebhookAtUtc = createdAt,
                LastWebhookAtUtc = createdAt,
                UpdatedAtUtc = createdAt,
            }
        );

        db.PullRequestLookups.Add(
            new PullRequestLookup
            {
                OrganizationId = organizationId,
                RepositoryId = repositoryId,
                TeamId = null,
                OwnerQualifiedRepoName = $"zeeq-fixture/{namespacePrefix}-{slug}",
                PullRequestNumber = number,
                PullRequestRecordId = prId,
                PullRequestCreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt,
            }
        );
    }

    var agentIds = new List<string>();
    var revisionIds = new List<string>();

    foreach (
        var (agentSlug, facet, prompt) in new[]
        {
            ("correctness", "Correctness", "Review for correctness and null safety."),
            ("security", "Security", "Review for injection and authorization defects."),
        }
    )
    {
        var agentId = $"cra_{namespacePrefix}_{slug}_{agentSlug}";
        var agent = new CodeReviewerAgent
        {
            Id = agentId,
            OrganizationId = organizationId,
            RepositoryId = repositoryId,
            DisplayName = $"{namespacePrefix} {slug} {facet}",
            ReviewFacet = facet,
            ModelTier = CodeReviewModelTier.High,
            Prompt = prompt,
            Enabled = true,
            // The correctness reviewer must not activate for the P5 file, while it keeps
            // activating for P3 and P6-P8 (all on src/Search.cs). The security reviewer keeps
            // the default activation so two reviewers can activate on the same case.
            ActivationConfiguration =
                agentSlug == "correctness"
                    ? new CodeReviewerActivationConfiguration
                    {
                        IncludedFiles = [],
                        ExcludedFiles =
                        [
                            new()
                            {
                                MatchType = CodeReviewFileNameMatchType.PathPrefix,
                                Pattern = "src/activation/",
                            },
                        ],
                    }
                    : new CodeReviewerActivationConfiguration(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.CodeReviewerAgents.Add(agent);
        agentIds.Add(agentId);

        var revisionId = $"crr_{namespacePrefix}_{slug}_{agentSlug}";
        db.CodeReviewerAgentRevisions.Add(
            new CodeReviewerAgentRevision
            {
                Id = revisionId,
                OrganizationId = organizationId,
                RepositoryId = repositoryId,
                AgentId = agentId,
                ContentHash = CodeReviewerAgentRevisionContent.ComputeHash(
                    CodeReviewerAgentRevisionContent.Capture(agent)
                ),
                Label = "fixture initial",
                Configuration = CodeReviewerAgentRevisionContent.Capture(agent),
                CreatedByUserId = "usr_0factor_e2e_alice",
                CreatedAtUtc = now,
            }
        );
        revisionIds.Add(revisionId);
    }

    if (!includeDataset)
    {
        return new(repositoryId, agentIds, revisionIds, [], [], []);
    }

    var datasetId = $"cred_{namespacePrefix}_alpha";
    db.CodeReviewEvaluationDatasets.Add(
        new CodeReviewEvaluationDataset
        {
            Id = datasetId,
            OrganizationId = organizationId,
            RepositoryId = repositoryId,
            Name = "Fixture backtesting dataset",
            Description = "Deterministic P1-P8 fixture cases for reviewer backtesting.",
            Version = 1,
            CreateRequestKey = $"{namespacePrefix}-create",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }
    );

    var correctnessAgentId = agentIds[0];
    var securityAgentId = agentIds[1];
    var caseIds = new List<string>();
    var artifactUris = new List<string>();

    foreach (var definition in FixtureCases.Definitions)
    {
        var caseId = $"crec_{namespacePrefix}_{definition.Number}";
        caseIds.Add(caseId);

        var snapshot = FixtureCases.BuildSnapshot(
            organizationId,
            repositoryId,
            $"zeeq-fixture/{namespacePrefix}-{slug}",
            definition,
            now
        );
        var bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);
        var path =
            $"reviewer-evaluations/{organizationId}/{repositoryId}/snap_{namespacePrefix}_{definition.Number}/snapshot.json";
        var uri = $"postgres://{path}";
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO zeeq.storage_objects
                (uri, organization_id, container, path, content_type, content_bytes,
                 metadata_json, created_at_utc, updated_at_utc)
            VALUES
                ({uri}, {organizationId}, {"Default"}, {path}, {"application/json"},
                 {bytes}, {"{}"}::jsonb, {now}, {now})
            """
        );
        artifactUris.Add(uri);

        db.CodeReviewEvaluationCases.Add(
            new CodeReviewEvaluationCase
            {
                Id = caseId,
                OrganizationId = organizationId,
                RepositoryId = repositoryId,
                DatasetId = datasetId,
                PullRequestNumber = definition.Number,
                PullRequestTitle = snapshot.Title,
                SnapshotStorageUri = uri,
                SnapshotSha256 = sha,
                SnapshotByteCount = bytes.Length,
                SnapshotSchemaVersion = CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
                BaseSha = snapshot.BaseSha,
                HeadSha = snapshot.HeadSha,
                PullRequestState = snapshot.State,
                CapturedAtUtc = snapshot.CapturedAtUtc,
                ChangedFileCount = snapshot.ChangedFileCount,
                OmittedFileCount = snapshot.OmittedFileCount,
                DeclaredChangedFileCount = snapshot.DeclaredChangedFileCount,
                ReferenceSets =
                [
                    .. FixtureCases.ReferenceSets(
                        definition,
                        correctnessAgentId,
                        securityAgentId,
                        now
                    ),
                ],
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
    }

    // Capture-only fixtures: PR 909 and 910 are browsable and resolvable without becoming
    // members of the curated dataset. They live in a separate archived capture dataset so the
    // dataset membership shown in curation stays exactly P1-P8.
    var captureDatasetId = $"cred_{namespacePrefix}_capture";
    db.CodeReviewEvaluationDatasets.Add(
        new CodeReviewEvaluationDataset
        {
            Id = captureDatasetId,
            OrganizationId = organizationId,
            RepositoryId = repositoryId,
            Name = "Fixture capture-only snapshots",
            Description = "Snapshots for browsable fixture pull requests that are not members.",
            Version = 1,
            CreateRequestKey = $"{namespacePrefix}-capture",
            ArchivedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }
    );

    foreach (var captureDefinition in FixtureCases.CaptureOnlyDefinitions)
    {
        var snapshot = FixtureCases.BuildSnapshot(
            organizationId,
            repositoryId,
            $"zeeq-fixture/{namespacePrefix}-{slug}",
            captureDefinition,
            now
        );
        var bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);
        var path =
            $"reviewer-evaluations/{organizationId}/{repositoryId}/snap_{namespacePrefix}_{captureDefinition.Number}/snapshot.json";
        var uri = $"postgres://{path}";
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO zeeq.storage_objects
                (uri, organization_id, container, path, content_type, content_bytes,
                 metadata_json, created_at_utc, updated_at_utc)
            VALUES
                ({uri}, {organizationId}, {"Default"}, {path}, {"application/json"},
                 {bytes}, {"{}"}::jsonb, {now}, {now})
            """
        );
        artifactUris.Add(uri);

        db.CodeReviewEvaluationCases.Add(
            new CodeReviewEvaluationCase
            {
                Id = $"crec_{namespacePrefix}_{captureDefinition.Number}",
                OrganizationId = organizationId,
                RepositoryId = repositoryId,
                DatasetId = captureDatasetId,
                PullRequestNumber = captureDefinition.Number,
                PullRequestTitle = snapshot.Title,
                SnapshotStorageUri = uri,
                SnapshotSha256 = sha,
                SnapshotByteCount = bytes.Length,
                SnapshotSchemaVersion = CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
                BaseSha = snapshot.BaseSha,
                HeadSha = snapshot.HeadSha,
                PullRequestState = snapshot.State,
                CapturedAtUtc = snapshot.CapturedAtUtc,
                ChangedFileCount = snapshot.ChangedFileCount,
                OmittedFileCount = snapshot.OmittedFileCount,
                DeclaredChangedFileCount = snapshot.DeclaredChangedFileCount,
                ReferenceSets = [],
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
        caseIds.Add($"crec_{namespacePrefix}_{captureDefinition.Number}");
    }

    return new(
        repositoryId,
        agentIds,
        revisionIds,
        [datasetId, captureDatasetId],
        caseIds,
        artifactUris
    );
}

async Task<int> CleanupAsync()
{
    if (!File.Exists(manifestPath))
    {
        throw new InvalidOperationException(
            $"No fixture manifest for {namespacePrefix}; refusing to guess what to delete."
        );
    }

    var stored = JsonSerializer.Deserialize<FixtureManifest>(
        await File.ReadAllTextAsync(manifestPath)
    )!;

    if (!string.Equals(stored.Namespace, namespacePrefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Fixture manifest namespace does not match this run.");
    }

    await db.Database.BeginTransactionAsync();

    // Delete exactly what the manifest owns. Runs created by browser/worker acceptance are
    // discovered through the manifest's dataset ids, then their results are removed first.
    var datasetIds = stored.Datasets.ToArray();
    var runIds =
        datasetIds.Length == 0
            ? []
            : await db
                .CodeReviewEvaluationRuns.AsNoTracking()
                .Where(run => datasetIds.Contains(run.DatasetId))
                .Select(run => run.Id)
                .ToArrayAsync();

    if (runIds.Length > 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_case_results WHERE run_id = ANY({runIds})"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_runs WHERE id = ANY({runIds})"
        );
    }

    if (datasetIds.Length > 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_cases WHERE dataset_id = ANY({datasetIds})"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_evaluation_datasets WHERE id = ANY({datasetIds})"
        );
    }

    var agentIds = stored.Agents.ToArray();
    if (agentIds.Length > 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_reviewer_agent_revisions WHERE agent_id = ANY({agentIds})"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_reviewer_agents WHERE id = ANY({agentIds})"
        );
    }

    var repositoryIds = stored.Repositories.ToArray();
    if (repositoryIds.Length > 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_pull_request_lookups WHERE repository_id = ANY({repositoryIds})"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_pull_request_records WHERE repository_id = ANY({repositoryIds})"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.code_review_repositories WHERE id = ANY({repositoryIds})"
        );
    }

    // Snapshot artifacts are recorded exactly. Candidate outputs written by accepted runs are
    // not known at seed time, so remove the repository artifact trees by prefix, which is
    // still scoped to fixture-owned repositories only.
    foreach (var uri in stored.Artifacts)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.storage_objects WHERE uri = {uri}"
        );
    }

    foreach (var repositoryId in repositoryIds)
    {
        var repository = await db
            .CodeRepositories.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == repositoryId);
        if (repository is null)
        {
            continue;
        }

        var prefix =
            $"postgres://reviewer-evaluations/{repository.OrganizationId}/{repository.Id}/";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM zeeq.storage_objects WHERE uri LIKE {prefix + "%"}"
        );
    }

    await db.Database.CommitTransactionAsync();

    var remaining =
        datasetIds.Length == 0
            ? 0
            : await db
                .CodeReviewEvaluationDatasets.AsNoTracking()
                .CountAsync(dataset => datasetIds.Contains(dataset.Id));
    if (remaining != 0)
    {
        throw new InvalidOperationException("Fixture cleanup left evaluation datasets behind.");
    }

    File.Delete(manifestPath);
    Console.WriteLine($"Fixture cleaned: {namespacePrefix}");
    return 0;
}

static bool IsLoopback(string host) =>
    host is "localhost" or "127.0.0.1" or "::1" or "host.docker.internal";

internal sealed record RepositoryFixture(
    string RepositoryId,
    IReadOnlyList<string> AgentIds,
    IReadOnlyList<string> RevisionIds,
    IReadOnlyList<string> DatasetIds,
    IReadOnlyList<string> CaseIds,
    IReadOnlyList<string> ArtifactUris
);

internal sealed record FixtureManifest(
    string Namespace,
    DateTimeOffset SeededAtUtc,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Revisions,
    IReadOnlyList<string> Datasets,
    IReadOnlyList<string> Cases,
    IReadOnlyList<string> Artifacts
);

internal sealed record FixtureCaseDefinition(
    int Number,
    string Title,
    string Patch,
    string ExpectedState,
    string[] Findings
);

internal static class FixtureCases
{
    private const string SearchPatch =
        "@@ -10,7 +10,9 @@ public static class Search\n-    return results.OrderBy(x => x.Score);\n+    return results\n+        .OrderByDescending(x => x.ExactMatch)\n+        .ThenByDescending(x => x.Score);";

    private const string RenamePatch =
        "@@ -1,3 +1,3 @@\n-public sealed class QueryParser\n+public sealed class SearchQueryParser";

    private const string CachePatch =
        "@@ -4,3 +4,4 @@\n     var key = BuildKey(input);\n+    key = key.Trim().ToLowerInvariant();\n     return cache.GetOrCreate(key);";

    private const string DocsPatch = "@@ -1,2 +1,3 @@\n # Search\n+Updated wording only.";

    /// <summary>
    /// Browsable pull requests that are not dataset members. They exist so Add PRs can resolve
    /// and capture without a curated row.
    /// </summary>
    public static readonly FixtureCaseDefinition[] CaptureOnlyDefinitions =
    [
        new(909, "Browsable clean candidate", CachePatch, "Unlabelled", []),
        new(910, "Browsable second candidate", SearchPatch, "Unlabelled", []),
    ];

    public static readonly FixtureCaseDefinition[] Definitions =
    [
        new(901, "Exact-match ranking regression", SearchPatch, "Expected", ["R1", "R2"]),
        new(902, "Clean rename refactor", RenamePatch, "ReviewedClean", []),
        new(903, "Unlabelled cache change", CachePatch, "Unlabelled", []),
        new(904, "Out-of-scope documentation change", DocsPatch, "Unlabelled", []),
        new(905, "Activation filter excludes change", CachePatch, "Unlabelled", []),
        new(906, "Reviewer failure case", SearchPatch, "Unlabelled", []),
        new(907, "Judge failure case", SearchPatch, "Expected", ["R1"]),
        new(908, "Concurrency barrier case", CachePatch, "Expected", ["R1"]),
    ];

    public static CodeReviewEvaluationSnapshot BuildSnapshot(
        string organizationId,
        string repositoryId,
        string ownerQualifiedName,
        FixtureCaseDefinition definition,
        DateTimeOffset now
    ) =>
        new(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            organizationId,
            repositoryId,
            ownerQualifiedName,
            definition.Number,
            definition.Title,
            "Fixture pull request for deterministic reviewer backtesting.",
            $"base-{definition.Number}",
            $"head-{definition.Number}",
            "e2e-alice-owner",
            $"https://github.com/{ownerQualifiedName}/pull/{definition.Number}",
            false,
            PullRequestState.Open,
            now,
            1,
            [
                new(
                    definition.Number switch
                    {
                        904 => "docs/README.md",
                        905 => "src/activation/ActivationOnly.cs",
                        _ => "src/Search.cs",
                    },
                    null,
                    CodeReviewFileMutationState.Modified,
                    definition.Patch,
                    false
                ),
            ]
        );

    public static IReadOnlyList<CodeReviewEvaluationReferenceSet> ReferenceSets(
        FixtureCaseDefinition definition,
        string correctnessAgentId,
        string securityAgentId,
        DateTimeOffset now
    )
    {
        var state = definition.ExpectedState switch
        {
            "Expected" => CodeReviewEvaluationReferenceState.Expected,
            "ReviewedClean" => CodeReviewEvaluationReferenceState.ReviewedClean,
            _ => CodeReviewEvaluationReferenceState.Unlabelled,
        };

        var findings = definition
            .Findings.Select(id => new CodeReviewEvaluationReferenceFinding(
                id,
                id == "R1"
                    ? "Exact matches must rank first"
                    : "Wildcard characters must be escaped",
                id == "R1"
                    ? "Ranking must prefer exact matches before score ordering."
                    : "User input containing wildcard characters must be escaped before matching.",
                CodeReviewEvaluationFindingSeverity.Major,
                "src/Search.cs",
                id == "R1" ? 12 : 24,
                CodeReviewEvaluationReferenceOrigin.Manual,
                null,
                null,
                "usr_0factor_e2e_alice",
                now
            ))
            .ToArray();

        return
        [
            new(correctnessAgentId, state, findings),
            // Deliberately different expectations for the second reviewer: a label leak across
            // reviewer ids must fail the acceptance run.
            new(securityAgentId, CodeReviewEvaluationReferenceState.Unlabelled, []),
        ];
    }
}
