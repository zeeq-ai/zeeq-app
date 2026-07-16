namespace Zeeq.Core.Identity;

/// <summary>
/// Endpoint metadata marking system-admin routes whose authorization denials are cloaked as 404.
/// </summary>
/// <remarks>
/// The authorization middleware result handler checks for this marker before
/// transforming challenge or forbid outcomes. Routes without this marker keep
/// the framework's default authorization behavior.
/// </remarks>
public sealed class HiddenAdminRouteMetadata;
