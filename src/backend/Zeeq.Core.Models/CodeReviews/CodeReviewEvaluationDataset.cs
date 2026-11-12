namespace Zeeq.Core.Models;

/// <summary>
/// Named, versioned set of pull request examples used to back-test reviewer agents.
/// </summary>
/// <remarks>
/// A dataset belongs to one repository and is reusable by every reviewer in that
/// repository. The name is a label, not an identity: <see cref="Version"/> is the
/// concurrency token that Save and run admission compare under a row lock, and
/// <see cref="CreateRequestKey"/> makes the initial create retry-safe.
/// Deleting a dataset archives it; run history and artifacts are never cascaded.
/// </remarks>
public sealed class CodeReviewEvaluationDataset : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context inherited from the repository mapping.</summary>
    public string? TeamId { get; init; }

    /// <summary>Repository mapping this dataset belongs to.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Human-readable dataset name shown in the curation slideover.</summary>
    public required string Name { get; set; }

    /// <summary>Optional operator description of what this dataset is meant to prove.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Monotonic content version, starting at <c>1</c> for a freshly created dataset.
    /// </summary>
    /// <remarks>
    /// Save requires the caller's expected version, installs staged membership and
    /// reference changes, then increments this value exactly once. Run admission
    /// copies the same version into its immutable manifest, so a run can never mix
    /// two dataset versions.
    /// </remarks>
    public long Version { get; set; } = 1;

    /// <summary>Client-supplied idempotency key for the initial create call.</summary>
    public required string CreateRequestKey { get; init; }

    /// <summary>Set when the dataset is archived; archived datasets keep their history.</summary>
    public DateTimeOffset? ArchivedAtUtc { get; set; }
}
