using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for repository-scoped MCP prompt activation and placeholder overrides.
/// </summary>
/// <remarks>
/// Rows are addressed by the natural key (organization, repository, library, document) rather than
/// the synthetic id. That is what lets the configuration UI save without first discovering whether a
/// row exists, and it matches the unique index that backs both read paths.
/// </remarks>
internal sealed class PostgresCodeRepositoryPromptConfigurationStore(PostgresDbContext db)
    : ICodeRepositoryPromptConfigurationStore
{
    private const string UniqueViolationSqlState = "23505";
    private const int MaximumUpsertAttempts = 2;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeRepositoryPromptConfiguration>> ListForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeRepositoryPromptConfigurations.TagWithOperationCallSite(
                "code_repository_prompt_configuration.list_for_repository"
            )
            .AsNoTracking()
            .Where(configuration =>
                configuration.OrganizationId == organizationId
                && configuration.RepositoryId == repositoryId
                && configuration.DisabledAtUtc == null
            )
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeRepositoryPromptConfiguration?> FindActiveForPromptAsync(
        string organizationId,
        string repositoryId,
        string libraryId,
        string documentId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeRepositoryPromptConfigurations.TagWithOperationCallSite(
                "code_repository_prompt_configuration.find_active_for_prompt"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                configuration =>
                    configuration.OrganizationId == organizationId
                    && configuration.RepositoryId == repositoryId
                    && configuration.LibraryId == libraryId
                    && configuration.DocumentId == documentId
                    // NOTE: This tombstone belongs to the prompt-configuration row, not the
                    // repository mapping. Paused repositories still resolve before this lookup;
                    // deleted prompt configs must not apply to MCP prompt rendering.
                    && configuration.DisabledAtUtc == null
                    && configuration.Active,
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<CodeRepositoryPromptConfiguration> UpsertAsync(
        CodeRepositoryPromptConfiguration configuration,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; attempt <= MaximumUpsertAttempts; attempt++)
        {
            var existing = await db
                .CodeRepositoryPromptConfigurations.TagWithOperationCallSite(
                    "code_repository_prompt_configuration.upsert_lookup"
                )
                .FirstOrDefaultAsync(
                    row =>
                        row.OrganizationId == configuration.OrganizationId
                        && row.RepositoryId == configuration.RepositoryId
                        && row.LibraryId == configuration.LibraryId
                        && row.DocumentId == configuration.DocumentId
                        // NOTE: Match the filtered unique index (`disabled_at_utc IS NULL`).
                        // Tombstoned rows are historical records and intentionally do not block a
                        // fresh live configuration for the same repository/prompt natural key.
                        && row.DisabledAtUtc == null,
                    cancellationToken
                );

            var now = DateTimeOffset.UtcNow;

            if (existing is not null)
            {
                existing.TeamId = configuration.TeamId;
                existing.Active = configuration.Active;
                existing.PlaceholderValues = configuration.PlaceholderValues;
                existing.UpdatedAtUtc = now;

                await db.SaveChangesAsync(cancellationToken);

                return existing;
            }

            var created = new CodeRepositoryPromptConfiguration
            {
                Id = "rpc_" + Guid.CreateVersion7().ToString("N"),
                OrganizationId = configuration.OrganizationId,
                TeamId = configuration.TeamId,
                RepositoryId = configuration.RepositoryId,
                LibraryId = configuration.LibraryId,
                DocumentId = configuration.DocumentId,
                Active = configuration.Active,
                PlaceholderValues = configuration.PlaceholderValues,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            db.CodeRepositoryPromptConfigurations.Add(created);

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return created;
            }
            catch (DbUpdateException ex)
                when (attempt < MaximumUpsertAttempts && IsUniqueViolation(ex))
            {
                // NOTE: This preserves the simple EF upsert path while handling the only expected
                // race: another writer inserts the same live natural key between our lookup and
                // SaveChanges. Detach the failed Added entity so the retry can reload and update
                // the winner instead of re-sending the duplicate insert.
                db.Entry(created).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            "Repository prompt configuration upsert retry exhausted."
        );
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolationSqlState };
}
