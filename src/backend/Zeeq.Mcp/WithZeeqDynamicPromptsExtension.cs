using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Zeeq.Mcp;

/// <summary>
/// Filter which sets up the dynamic prompts.
/// </summary>
internal static class WithZeeqDynamicPromptsExtension
{
    extension(IMcpServerBuilder server)
    {
        /// <summary>
        /// Configures the Zeeq MCP server for dynamic prompts.
        /// </summary>
        /// <returns></returns>
        public IMcpServerBuilder WithZeeqDynamicPrompts()
        {
            server
                // List the custom prompts from the document libraries which are
                // marked as prompts.
                .WithListPromptsHandler(
                    (request, cancellation) =>
                    {
                        var serviceProvider =
                            request.Services
                            ?? throw new McpProtocolException(
                                "MCP request services are unavailable.",
                                McpErrorCode.InternalError
                            );
                        var prompts = serviceProvider.GetRequiredService<IDynamicPromptsService>();

                        return prompts.ListPromptsAsync(request.User, cancellation);
                    }
                )
                // Get the requested prompt from the document library
                .WithGetPromptHandler(
                    (request, cancellation) =>
                    {
                        var serviceProvider =
                            request.Services
                            ?? throw new McpProtocolException(
                                "MCP request services are unavailable.",
                                McpErrorCode.InternalError
                            );
                        var prompts = serviceProvider.GetRequiredService<IDynamicPromptsService>();

                        return prompts.GetPromptAsync(request.User, request.Params, cancellation);
                    }
                );

            return server;
        }
    }
}
