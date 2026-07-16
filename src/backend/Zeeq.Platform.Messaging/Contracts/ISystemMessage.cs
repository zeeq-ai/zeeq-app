namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker for platform messages that are not owned by a tenant.
/// </summary>
/// <remarks>
/// System messages route to the system queue table and bypass tenant-tier
/// bucket selection.
/// </remarks>
public interface ISystemMessage;
