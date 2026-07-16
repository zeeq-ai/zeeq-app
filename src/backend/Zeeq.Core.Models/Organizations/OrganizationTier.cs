namespace Zeeq.Core.Models;

/// <summary>
/// Tenant service tier used to allocate queue capacity for organization-owned work.
/// </summary>
public enum OrganizationTier
{
    /// <summary>
    /// Baseline tier for standard organizations.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Reserved-capacity tier for high-value organizations or urgent workloads.
    /// </summary>
    Priority = 1,

    /// <summary>
    /// Reduced-capacity tier for trial, bulk, or low-urgency organizations.
    /// </summary>
    Low = 2,
}
