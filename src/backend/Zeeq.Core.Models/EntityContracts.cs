using System.ComponentModel.DataAnnotations.Schema;

namespace Zeeq.Core.Models;

/// <summary>
/// Entity with a synthetic stable string identifier.
/// </summary>
public interface IEntity
{
    /// <summary>
    /// Stable entity identifier.
    /// </summary>
    string Id { get; }
}

/// <summary>
/// Entity that records when it was created.
/// </summary>
public interface ICreatedEntity
{
    /// <summary>
    /// UTC timestamp when the entity was created.
    /// </summary>
    DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>
/// Entity that records when it was last updated.
/// </summary>
public interface IUpdatedEntity
{
    /// <summary>
    /// UTC timestamp when the entity was last updated.
    /// </summary>
    DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Entity that can be disabled without deleting the row.
/// </summary>
public interface ICanBeDisabled
{
    /// <summary>
    /// Set when the entity is disabled; <see langword="null"/> means active.
    /// </summary>
    DateTimeOffset? DisabledAtUtc { get; set; }
}

/// <summary>
/// Entity scoped to an organization.
/// </summary>
public interface IOrganizationScopedEntity
{
    /// <summary>
    /// Organization that owns the entity.
    /// </summary>
    string OrganizationId { get; }
}

/// <summary>
/// Entity scoped to a team inside an organization.
/// </summary>
public interface ITeamScopedEntity : IOrganizationScopedEntity
{
    /// <summary>
    /// Team that owns the entity.
    /// </summary>
    string TeamId { get; }
}

/// <summary>
/// Base type for rows with a synthetic stable string identifier.
/// </summary>
[NotMapped]
public abstract class EntityBase : IEntity
{
    /// <inheritdoc />
    public required string Id { get; init; }
}

/// <summary>
/// Base type for stable domain rows that are created once and can be disabled.
/// </summary>
[NotMapped]
public abstract class DomainEntityBase : EntityBase, ICreatedEntity, ICanBeDisabled
{
    /// <inheritdoc />
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <inheritdoc />
    public DateTimeOffset? DisabledAtUtc { get; set; }
}

/// <summary>
/// Base type for domain rows whose display or configuration data can change.
/// </summary>
[NotMapped]
public abstract class MutableDomainEntityBase : DomainEntityBase, IUpdatedEntity
{
    /// <inheritdoc />
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
