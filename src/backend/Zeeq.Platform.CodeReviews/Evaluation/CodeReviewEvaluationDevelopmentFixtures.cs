using System.Security.Claims;
using Microsoft.Extensions.Hosting;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Explicit repository allowlist for a local deterministic evaluation fixture.</summary>
public sealed class CodeReviewEvaluationFixtureRepositories
{
    private readonly HashSet<(string OrganizationId, string RepositoryId)> _ids;

    /// <summary>Creates a fixture allowlist only in a development host.</summary>
    public CodeReviewEvaluationFixtureRepositories(
        IHostEnvironment environment,
        IEnumerable<(string OrganizationId, string RepositoryId)> repositories
    )
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Evaluation fixtures require Development.");
        _ids = new(
            repositories.Where(x =>
                !string.IsNullOrWhiteSpace(x.OrganizationId)
                && !string.IsNullOrWhiteSpace(x.RepositoryId)
            )
        );
        if (_ids.Count == 0)
            throw new InvalidOperationException("Explicit fixture repository ids are required.");
    }

    /// <summary>True only for an explicitly configured fixture repository id.</summary>
    public bool Contains(string organizationId, string? id) =>
        id is not null && _ids.Contains((organizationId, id));
}

/// <summary>Development-only, exact-repository reviewer double. All other repositories use the real executor.</summary>
public sealed class CodeReviewEvaluationFixtureReviewer(
    CodeReviewEvaluationFixtureRepositories fixtures,
    CodeReviewAgentExecutor real
) : ICodeReviewAgentExecutor
{
    /// <inheritdoc />
    public Task<string> ExecuteAsync(
        string organizationId,
        IReadOnlyList<CodeReviewerRuntimeAgent> activeReviewers,
        bool noAgentsActivated,
        CodeReviewUserPrompt codeReviewUserPrompt,
        IReadOnlyList<CodeReviewPreviousReview> previousReviews,
        ClaimsPrincipal callerIdentity,
        CodeReviewTelemetryContext telemetry,
        CodeReviewExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        if (!fixtures.Contains(organizationId, telemetry.RepositoryId))
            return real.ExecuteAsync(
                organizationId,
                activeReviewers,
                noAgentsActivated,
                codeReviewUserPrompt,
                previousReviews,
                callerIdentity,
                telemetry,
                options,
                cancellationToken
            );
        if (
            options.Mode is not (CodeReviewExecutionMode.Evaluation or CodeReviewExecutionMode.Test)
            || activeReviewers.Count != 1
        )
            throw new InvalidOperationException(
                "Fixture repository only supports deterministic evaluation cases."
            );
        cancellationToken.ThrowIfCancellationRequested();
        var number = options.FixturePullRequestNumber;
        if (number is 906)
            throw new InvalidOperationException("Controlled fixture reviewer failure.");
        if (number is not (901 or 902 or 903 or 907 or 908))
            throw new InvalidOperationException("Unknown fixture case.");
        var findings = new List<CodeReviewFindingOutput>();
        if (number is 901 or 907 or 908)
            findings.Add(
                new()
                {
                    Level = CodeReviewFindingLevel.Major,
                    File = "src/Search.cs",
                    Line = 12,
                    Summary = "Exact matches must rank first",
                    Details = "Prefer exact matches before score ordering.",
                }
            );
        if (number is 901 or 903)
            findings.Add(
                new()
                {
                    Level = CodeReviewFindingLevel.Minor,
                    File = "src/Search.cs",
                    Line = 24,
                    Summary = "Cache-key normalization",
                    Details = "Normalize cache keys consistently.",
                }
            );
        return Task.FromResult(
            CodeReviewXmlOutputValidator.Serialize(
                new CodeReviewOutputDocument
                {
                    Reviews =
                    [
                        new()
                        {
                            Agent = activeReviewers[0].DisplayName,
                            Facet = activeReviewers[0].ReviewFacet,
                            Summary = "Deterministic fixture review",
                            Details = "Synthetic fixture output.",
                            Findings = findings,
                        },
                    ],
                }
            )
        );
    }
}

/// <summary>Development-only judge double scoped to explicitly configured fixture repositories.</summary>
public sealed class CodeReviewEvaluationFixtureJudge(
    CodeReviewEvaluationFixtureRepositories fixtures,
    CodeReviewEvaluationJudge real
) : ICodeReviewEvaluationJudge
{
    /// <inheritdoc />
    public Task<CodeReviewEvaluationJudgeResult> GradeAsync(
        string organizationId,
        CodeReviewEvaluationModelSelection model,
        CodeReviewEvaluationSnapshot snapshot,
        IReadOnlyList<CodeReviewEvaluationReferenceFinding> references,
        IReadOnlyList<CodeReviewEvaluationCandidate> candidates,
        CancellationToken cancellationToken
    )
    {
        if (snapshot.OrganizationId != organizationId)
            throw new InvalidOperationException("Fixture snapshot organization mismatch.");
        if (!fixtures.Contains(organizationId, snapshot.RepositoryId))
            return real.GradeAsync(
                organizationId,
                model,
                snapshot,
                references,
                candidates,
                cancellationToken
            );
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.PullRequestNumber == 907)
            throw new InvalidOperationException("Controlled fixture judge failure.");
        if (snapshot.PullRequestNumber is not (901 or 902 or 908))
            throw new InvalidOperationException("Unknown fixture case.");
        var json = snapshot.PullRequestNumber is 901 or 908
            ? """{"matches":[{"referenceId":"R1","candidateId":"finding_1","reason":"Same issue"}]}"""
            : """{"matches":[]}""";
        var grade = CodeReviewEvaluationGradeValidator.Validate(
            json,
            [.. references.Select(x => x.Id)],
            [.. candidates.Select(x => x.Id)]
        );
        return Task.FromResult(new CodeReviewEvaluationJudgeResult(grade, "fixture", null, null));
    }
}
