namespace Zeeq.Core.Models;

/// <summary>
/// Short-lived lease that serializes GitHub comment writes for one logical comment target.
/// </summary>
/// <remarks>
/// This model backs an unlogged Postgres table. It is intentionally a transient
/// coordination artifact, not durable business state: if Postgres crashes and
/// clears unlogged data, the next queued writer can acquire the lease and repair
/// GitHub from the current comment body plus the authoritative render source.
///
/// The <see cref="LeaseKey" /> is generated from the comment target selector
/// and carries the organization, repository, pull request, target kind, and
/// scope. The table keeps only that compact key because rows are expected to be
/// created, renewed, and deleted quickly during one GitHub API write cycle.
/// </remarks>
public sealed class GitHubCommentLease
{
    /// <summary>
    /// Stable logical target key for the GitHub comment being written.
    /// </summary>
    public required string LeaseKey { get; init; }

    /// <summary>
    /// Process-local worker identity that currently owns the lease.
    /// </summary>
    public required string WorkerId { get; set; }

    /// <summary>
    /// Application-clock timestamp when the current worker acquired the lease.
    /// </summary>
    public DateTimeOffset AcquiredAtUtc { get; set; }

    /// <summary>
    /// Application-clock timestamp after which another worker may take over.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
