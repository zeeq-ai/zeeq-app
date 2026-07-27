using System.Security.Claims;
using ModelContextProtocol.Protocol;

namespace Zeeq.Mcp;

/// <summary>
/// Resolves organization-scoped Zeeq documents exposed as dynamic MCP prompts.
/// </summary>
public interface IDynamicPromptsService
{
    /// <summary>
    /// Lists prompts available to the authenticated caller.
    /// </summary>
    ValueTask<ListPromptsResult> ListPromptsAsync(
        ClaimsPrincipal? user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Retrieves one prompt by MCP prompt name.
    /// </summary>
    ValueTask<GetPromptResult> GetPromptAsync(
        ClaimsPrincipal? user,
        GetPromptRequestParams? request,
        CancellationToken cancellationToken
    );
}
