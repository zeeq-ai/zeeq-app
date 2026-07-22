using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="ISystemOrganizationManagementStore" />
internal sealed class PostgresSystemOrganizationManagementStore(PostgresDbContext db)
    : ISystemOrganizationManagementStore
{
    /// <inheritdoc />
    public async Task<SystemOrganizationPage<SystemOrganizationSummary>> ListOrganizationsAsync(
        int page,
        int pageSize,
        string? query,
        CancellationToken ct
    )
    {
        ValidatePagination(page, pageSize);

        var normalizedQuery = NormalizeQuery(query);
        var organizationsQuery = ApplyOrganizationMatch(
            db.Organizations.TagWithOperationCallSite("system_org.list").AsNoTracking(),
            normalizedQuery
        );

        var totalCount = await organizationsQuery.CountAsync(ct);
        var offset = (page - 1) * pageSize;
        var rows = await organizationsQuery
            .OrderByDescending(organization => organization.CreatedAtUtc)
            .ThenBy(organization => organization.Id)
            .Skip(offset)
            .Take(pageSize)
            .GroupJoin(
                db.Users.TagWithOperationCallSite("system_org.list_creators").AsNoTracking(),
                organization => organization.CreatedByUserId,
                creator => creator.Id,
                (organization, creators) =>
                    new { organization, creator = creators.FirstOrDefault() }
            )
            .Select(row => new OrganizationRow(
                row.organization.Id,
                row.organization.DisplayName,
                row.organization.Slug,
                row.organization.IconUrl,
                row.organization.CreatedByUserId,
                row.creator == null ? string.Empty : row.creator.DisplayName,
                row.creator == null ? null : row.creator.Email,
                row.creator == null ? null : row.creator.PictureUrl,
                row.organization.CreatedAtUtc,
                row.organization.UpdatedAtUtc,
                row.organization.ActivatedAtUtc,
                row.organization.DisabledAtUtc,
                row.organization.Tier,
                row.organization.LlmConfiguration
            ))
            .ToArrayAsync(ct);

        var memberCounts = await FindMemberCountsAsync(rows.Select(row => row.Id).ToArray(), ct);
        var summaries = rows.Select(row => row.ToSummary(memberCounts.GetValueOrDefault(row.Id)))
            .ToArray();

        return new SystemOrganizationPage<SystemOrganizationSummary>(
            summaries,
            page,
            pageSize,
            totalCount
        );
    }

    /// <inheritdoc />
    public async Task<SystemOrganizationDetails?> FindOrganizationAsync(
        string orgId,
        CancellationToken ct
    )
    {
        var row = await db
            .Organizations.TagWithOperationCallSite("system_org.details")
            .AsNoTracking()
            .Where(organization => organization.Id == orgId)
            .GroupJoin(
                db.Users.TagWithOperationCallSite("system_org.details_creator").AsNoTracking(),
                organization => organization.CreatedByUserId,
                creator => creator.Id,
                (organization, creators) =>
                    new { organization, creator = creators.FirstOrDefault() }
            )
            .Select(row => new OrganizationRow(
                row.organization.Id,
                row.organization.DisplayName,
                row.organization.Slug,
                row.organization.IconUrl,
                row.organization.CreatedByUserId,
                row.creator == null ? string.Empty : row.creator.DisplayName,
                row.creator == null ? null : row.creator.Email,
                row.creator == null ? null : row.creator.PictureUrl,
                row.organization.CreatedAtUtc,
                row.organization.UpdatedAtUtc,
                row.organization.ActivatedAtUtc,
                row.organization.DisabledAtUtc,
                row.organization.Tier,
                row.organization.LlmConfiguration
            ))
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var memberCounts = await FindMemberCountsAsync([row.Id], ct);

        return row.ToDetails(memberCounts.GetValueOrDefault(row.Id));
    }

    /// <inheritdoc />
    public async Task<SystemOrganizationPage<SystemOrganizationMember>> ListMembersAsync(
        string orgId,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        ValidatePagination(page, pageSize);

        var membersQuery = db
            .OrganizationMemberships.TagWithOperationCallSite("system_org.members")
            .AsNoTracking()
            .Where(membership =>
                membership.OrganizationId == orgId
                && membership.UserId != null
                && membership.Status == MembershipStatus.Active
                && membership.DisabledAtUtc == null
            );

        var totalCount = await membersQuery.CountAsync(ct);
        var offset = (page - 1) * pageSize;
        var members = await membersQuery
            .Join(
                db.Users.TagWithOperationCallSite("system_org.members_users").AsNoTracking(),
                membership => membership.UserId!,
                user => user.Id,
                (membership, user) => new { membership, user }
            )
            .OrderBy(row => row.user.DisplayName)
            .ThenBy(row => row.user.Id)
            .Skip(offset)
            .Take(pageSize)
            .Select(row => new SystemOrganizationMember(
                row.user.Id,
                row.user.DisplayName,
                row.user.Email,
                row.user.PictureUrl,
                row.membership.Role,
                row.membership.CreatedAtUtc
            ))
            .ToArrayAsync(ct);

        return new SystemOrganizationPage<SystemOrganizationMember>(
            members,
            page,
            pageSize,
            totalCount
        );
    }

    /// <inheritdoc />
    public async Task<SystemOrganizationDetails?> UpdateOrganizationAdminStateAsync(
        string orgId,
        bool? active,
        OrganizationTier? tier,
        CancellationToken ct
    )
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var organization = await db
            .Organizations.TagWithOperationCallSite("system_org.update_state")
            .SingleOrDefaultAsync(organization => organization.Id == orgId, ct);

        if (organization is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var currentlyActive =
            organization.ActivatedAtUtc is not null && organization.DisabledAtUtc is null;
        if (active is true && !currentlyActive)
        {
            organization.ActivatedAtUtc = now;
            organization.DisabledAtUtc = null;
        }
        else if (active is false && currentlyActive)
        {
            organization.ActivatedAtUtc = null;
            organization.DisabledAtUtc = now;
        }

        if (tier is { } requestedTier)
        {
            organization.Tier = requestedTier;
        }

        organization.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        // NOTE: The final read intentionally uses the same DbContext because
        // FindOrganizationAsync projects scalar values through AsNoTracking()
        // into OrganizationRow; it does not return the tracked Organization
        // entity mutated above.
        return await FindOrganizationAsync(orgId, ct);
    }

    private static IQueryable<Organization> ApplyOrganizationMatch(
        IQueryable<Organization> query,
        string? normalizedQuery
    )
    {
        if (normalizedQuery is null)
        {
            return query;
        }

        var pattern = "%" + EscapeLikePattern(normalizedQuery) + "%";

        return query.Where(organization =>
            EF.Functions.ILike(organization.DisplayName, pattern, "\\")
            || EF.Functions.ILike(organization.Id, pattern, "\\")
            || (organization.Slug != null && EF.Functions.ILike(organization.Slug, pattern, "\\"))
        );
    }

    private async Task<IReadOnlyDictionary<string, int>> FindMemberCountsAsync(
        string[] organizationIds,
        CancellationToken ct
    )
    {
        if (organizationIds.Length == 0)
        {
            return new Dictionary<string, int>();
        }

        return await db
            .OrganizationMemberships.TagWithOperationCallSite("system_org.member_counts")
            .AsNoTracking()
            .Where(membership =>
                organizationIds.Contains(membership.OrganizationId)
                && membership.UserId != null
                && membership.Status == MembershipStatus.Active
                && membership.DisabledAtUtc == null
            )
            .GroupBy(membership => membership.OrganizationId)
            .Select(group => new { OrganizationId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.OrganizationId, row => row.Count, ct);
    }

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return query.Trim();
    }

    private static string EscapeLikePattern(string query) =>
        query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void ValidatePagination(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
    }

    private sealed record OrganizationRow(
        string Id,
        string DisplayName,
        string? Slug,
        string? IconUrl,
        string CreatorUserId,
        string CreatorDisplayName,
        string? CreatorEmail,
        string? CreatorPictureUrl,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? ActivatedAtUtc,
        DateTimeOffset? DisabledAtUtc,
        OrganizationTier Tier,
        OrganizationLlmConfiguration LlmConfiguration
    )
    {
        public SystemOrganizationSummary ToSummary(int memberCount)
        {
            var snapshot = ToSnapshot(memberCount);

            return new(
                snapshot.Id,
                snapshot.DisplayName,
                snapshot.Slug,
                snapshot.IconUrl,
                snapshot.Creator,
                snapshot.CreatedAtUtc,
                snapshot.UpdatedAtUtc,
                snapshot.ActivatedAtUtc,
                snapshot.DisabledAtUtc,
                snapshot.MemberCount,
                snapshot.Tier,
                snapshot.LlmConfiguration
            );
        }

        public SystemOrganizationDetails ToDetails(int memberCount)
        {
            var snapshot = ToSnapshot(memberCount);

            return new(
                snapshot.Id,
                snapshot.DisplayName,
                snapshot.Slug,
                snapshot.IconUrl,
                snapshot.Creator,
                snapshot.CreatedAtUtc,
                snapshot.UpdatedAtUtc,
                snapshot.ActivatedAtUtc,
                snapshot.DisabledAtUtc,
                snapshot.MemberCount,
                snapshot.Tier,
                snapshot.LlmConfiguration
            );
        }

        private OrganizationSnapshot ToSnapshot(int memberCount) =>
            new(
                Id,
                DisplayName,
                Slug,
                IconUrl,
                ToCreator(),
                CreatedAtUtc,
                UpdatedAtUtc,
                ActivatedAtUtc,
                DisabledAtUtc,
                memberCount,
                Tier,
                ToLlmConfiguration(LlmConfiguration)
            );

        private SystemOrganizationCreator ToCreator() =>
            new(CreatorUserId, CreatorDisplayName, CreatorEmail, CreatorPictureUrl);
    }

    private sealed record OrganizationSnapshot(
        string Id,
        string DisplayName,
        string? Slug,
        string? IconUrl,
        SystemOrganizationCreator Creator,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? ActivatedAtUtc,
        DateTimeOffset? DisabledAtUtc,
        int MemberCount,
        OrganizationTier Tier,
        SystemOrganizationLlmConfiguration LlmConfiguration
    );

    private static SystemOrganizationLlmConfiguration ToLlmConfiguration(
        OrganizationLlmConfiguration configuration
    ) =>
        new(
            ToLlmTier("Fast", configuration.Fast),
            ToLlmTier("High", configuration.High),
            ToLlmTier("Max", configuration.Max)
        );

    private static SystemOrganizationLlmTier ToLlmTier(
        string tier,
        OrganizationLlmTierConfiguration configuration
    ) =>
        new(
            tier,
            configuration.Provider,
            configuration.Model,
            configuration.Endpoint,
            UsesManagedKey: configuration.KeyId is not null,
            configuration.KeyId
        );
}
