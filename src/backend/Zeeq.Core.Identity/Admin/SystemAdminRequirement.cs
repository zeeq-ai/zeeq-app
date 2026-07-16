using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Core.Identity;

/// <summary>
/// Authorization requirement for routes that require live system-admin status.
/// </summary>
/// <remarks>
/// This requirement intentionally does not use role claims. It delegates to
/// <see cref="SystemAdminEvaluator"/> so admin grants and revocations are read
/// from current configuration on every request.
/// </remarks>
public sealed class SystemAdminRequirement : IAuthorizationRequirement;
