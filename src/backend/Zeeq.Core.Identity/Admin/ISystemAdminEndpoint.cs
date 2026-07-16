using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Core.Identity;

/// <summary>
/// Marker interface for endpoint slices that must be mapped under the system-admin route group.
/// </summary>
/// <remarks>
/// Runtime endpoint mapping passes these endpoints the `/api/v1/admin` group,
/// which already carries the live system-admin authorization requirement and
/// hidden-route metadata. Admin endpoint implementations should map relative to
/// that group and should not apply their own weaker authorization policy.
/// </remarks>
public interface ISystemAdminEndpoint : IEndpoint { }
