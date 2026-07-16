namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Stable storage key for the short-lived GitHub comment write lease.
/// </summary>
/// <remarks>
/// The key wraps <see cref="GitHubCommentTargetSelector" /> so queue handlers
/// and comment writers can pass the same logical target through the system while
/// stores use a compact string value. This type is deliberately small and can be
/// moved to a shared coordination package if more platform areas need leases.
/// </remarks>
public readonly record struct GitHubCommentLeaseKey(GitHubCommentTargetSelector Target)
{
    /// <summary>
    /// Returns the compact key persisted in the lease table.
    /// </summary>
    public override string ToString() => Target.ToStorageKey();
}
