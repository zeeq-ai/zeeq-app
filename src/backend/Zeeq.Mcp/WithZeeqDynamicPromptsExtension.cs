using Microsoft.Extensions.DependencyInjection;
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
                        // TODO: Get the prompt resolution service

                        return ValueTask.FromResult(new ListPromptsResult());
                    }
                )
                // Get the requested prompt from the document library
                .WithGetPromptHandler(
                    (request, cancellation) =>
                    {
                        // TODO: Get the prompt retrieval service and request the prompt which can include transforms

                        return ValueTask.FromResult(new GetPromptResult());
                    }
                );

            return server;
        }
    }
}
