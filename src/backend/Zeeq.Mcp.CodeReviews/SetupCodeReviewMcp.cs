using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Mcp.CodeReviews;

/// <summary>
/// Service registration for MCP code-review tools.
/// </summary>
public static class SetupCodeReviewMcp
{
    /// <summary>
    /// Registers MCP code-review services.
    /// </summary>
    public static IServiceCollection AddZeeqCodeReviewMcp(this IServiceCollection services)
    {
        // TODO(code-review-mcp): register upload-token services, artifact cleanup, and review runner bridge in Phase 8.
        return services;
    }
}
