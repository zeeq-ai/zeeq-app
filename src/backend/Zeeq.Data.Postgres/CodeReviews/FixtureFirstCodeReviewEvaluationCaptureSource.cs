using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Development-only capture source that serves seeded fixture snapshots instead of GitHub.
/// </summary>
/// <remarks>
/// Scope is decided by the exact <see cref="CodeReviewEvaluationFixtureRepositories"/> allowlist,
/// never by a name pattern. Inside the allowlist, capture reads the owned snapshot artifact the
/// fixture seeder wrote; it never falls back to GitHub, so a fixture run cannot create provider
/// traffic or fabricate production history. Anything outside the allowlist delegates to the real
/// provider source unchanged.
///
/// This type must only be registered in the Development environment and must be the last
/// registration of <see cref="ICodeReviewEvaluationPullRequestCaptureSource"/>.
/// </remarks>
internal sealed class FixtureFirstCodeReviewEvaluationCaptureSource(
    ICodeReviewEvaluationProviderCaptureSource inner,
    CodeReviewEvaluationFixtureRepositories fixtures,
    PostgresDbContext db,
    ICodeReviewEvaluationArtifactStore artifacts
) : ICodeReviewEvaluationPullRequestCaptureSource
{
    /// <inheritdoc />
    public async Task<CodeReviewEvaluationSnapshot> CaptureAsync(
        CodeReviewEvaluationCaptureRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!fixtures.Contains(request.OrganizationId, request.RepositoryId))
        {
            return await inner.CaptureAsync(request, cancellationToken);
        }

        var snapshot = await FixtureSnapshotLookup.FindAsync(
            db,
            artifacts,
            request.OrganizationId,
            request.RepositoryId,
            request.PullRequestNumber,
            cancellationToken
        );

        // A resolve is a new staging attempt even when the bytes are unchanged.
        return snapshot with
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>
/// Development-only production-source decorator that serves seeded fixture snapshots.
/// </summary>
/// <remarks>
/// The quick-test path uses the production <see cref="ICodeReviewPullRequestSource"/>. This
/// decorator keeps that path offline for exactly allowlisted fixture repositories, reusing the
/// verified envelope projection. A fixture repository with no seeded case fails closed.
/// </remarks>
internal sealed class FixtureFirstCodeReviewPullRequestSource(
    ICodeReviewProviderPullRequestSource inner,
    CodeReviewEvaluationFixtureRepositories fixtures,
    PostgresDbContext db,
    ICodeReviewEvaluationArtifactStore artifacts
) : ICodeReviewPullRequestSource
{
    /// <inheritdoc />
    public async Task<CodeReviewPullRequestSnapshot> GetPullRequestAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    )
    {
        var repository = await db
            .CodeRepositories.TagWithOperationCallSite(
                "code_review_fixture_pull_request_source_repository"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == message.OrganizationId
                    && candidate.OwnerQualifiedName == message.OwnerQualifiedRepoName
                    && candidate.DisabledAtUtc == null,
                cancellationToken
            );

        if (repository is null || !fixtures.Contains(message.OrganizationId, repository.Id))
        {
            return await inner.GetPullRequestAsync(message, cancellationToken);
        }

        var snapshot = await FixtureSnapshotLookup.FindAsync(
            db,
            artifacts,
            message.OrganizationId,
            repository.Id,
            message.PullRequestNumber,
            cancellationToken
        );

        return snapshot.ToPullRequestSnapshot();
    }
}

/// <summary>
/// Shared lookup for seeded fixture snapshots.
/// </summary>
/// <remarks>
/// Snapshots live on dataset cases. Capture-only pull requests (browsable but not yet a dataset
/// member) live in a fixture capture dataset, so curation can resolve them without pretending
/// they are membership rows in the curated dataset.
/// </remarks>
internal static class FixtureSnapshotLookup
{
    /// <summary>Finds the newest seeded snapshot for one fixture pull request.</summary>
    public static async Task<CodeReviewEvaluationSnapshot> FindAsync(
        PostgresDbContext db,
        ICodeReviewEvaluationArtifactStore artifacts,
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        var item = await db
            .CodeReviewEvaluationCases.TagWithOperationCallSite("code_review_fixture_snapshot")
            .AsNoTracking()
            .Where(candidate =>
                candidate.OrganizationId == organizationId
                && candidate.RepositoryId == repositoryId
                && candidate.PullRequestNumber == pullRequestNumber
                && candidate.DisabledAtUtc == null
                && candidate.SnapshotSha256 != string.Empty
            )
            .OrderByDescending(candidate => candidate.CapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (item is null)
        {
            // Fail closed: a fixture repository must never reach GitHub during acceptance.
            throw new CodeReviewEvaluationCaptureException(
                "pr_not_found",
                "No seeded fixture snapshot exists for this pull request. Re-run the fixture seeder."
            );
        }

        return await artifacts.ReadSnapshotAsync(
            organizationId,
            repositoryId,
            new CodeReviewEvaluationArtifact(
                item.SnapshotStorageUri,
                item.SnapshotSha256,
                item.SnapshotByteCount
            ),
            cancellationToken
        );
    }
}

/// <summary>
/// Development-only registration for fixture capture and quick-test sources.
/// </summary>
/// <remarks>
/// The host must call this only when <c>IHostEnvironment.IsDevelopment()</c>, only after the
/// GitHub integration is registered, and only with the exact organization/repository pairs the
/// fixture seeder owns. Production registration must not include it; a registration test asserts
/// that. The local-database and environment guards live in the host.
/// </remarks>
public static class SetupCodeReviewEvaluationFixtures
{
    /// <summary>
    /// Registers both fixture-first source decorators.
    /// </summary>
    /// <remarks>
    /// The host owns the exact allowlist instance
    /// (<see cref="CodeReviewEvaluationFixtureRepositories"/>, registered with the environment and
    /// explicit organization/repository pairs) and must register it before calling this. This
    /// extension adds only the two decorators, so there is no second allowlist contract.
    /// </remarks>
    public static IServiceCollection AddZeeqCodeReviewEvaluationFixtures(
        this IServiceCollection services
    )
    {
        services.AddScoped<
            ICodeReviewEvaluationPullRequestCaptureSource,
            FixtureFirstCodeReviewEvaluationCaptureSource
        >();
        services.AddScoped<ICodeReviewPullRequestSource, FixtureFirstCodeReviewPullRequestSource>();

        return services;
    }
}
