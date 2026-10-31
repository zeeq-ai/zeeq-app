using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.CodeReviews;

// SaveChanges flushes the entire context, not only the operation's target repository.
internal static class RepositoryLinkGuard
{
    public static void RejectPending(
        PostgresDbContext db,
        string? allowedOrganization = null,
        string? allowedRepository = null,
        CodeRepository? allowedNew = null
    )
    {
        db.ChangeTracker.DetectChanges();
        if (
            db
                .ChangeTracker.Entries<CodeRepository>()
                .Any(entry =>
                    (
                        entry.State == EntityState.Added
                        && entry.Entity.LibraryIds.Length > 0
                        && !ReferenceEquals(entry.Entity, allowedNew)
                    )
                    || (
                        entry.State != EntityState.Added
                        && entry.Property(e => e.LibraryIds).IsModified
                        && (
                            entry.Entity.OrganizationId != allowedOrganization
                            || entry.Entity.Id != allowedRepository
                        )
                    )
                )
        )
            throw new InvalidOperationException(
                "An unrelated repository has pending library link changes."
            );
    }
}
