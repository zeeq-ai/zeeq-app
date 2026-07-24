namespace Zeeq.Core.Identity;

/// <summary>
/// Caller-facing identity endpoint error with a stable code.
/// </summary>
/// <param name="Code">Machine-readable error code.</param>
/// <param name="Message">Human-readable explanation.</param>
internal sealed record IdentityEndpointError(string Code, string Message);
