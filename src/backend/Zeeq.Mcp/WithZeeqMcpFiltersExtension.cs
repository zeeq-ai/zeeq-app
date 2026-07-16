using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp;

/// <summary>
/// Set up filters for the MCP runtime.
/// See: https://modelcontextprotocol.github.io/csharp-sdk/concepts/filters.html
/// </summary>
internal static class SetupMcpFiltersExtensions
{
    private static readonly Counter<int> UserAgentCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_user_agent_counter");

    private static readonly Counter<int> ToolCallCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_tool_call_counter");

    private static readonly Counter<int> AgentClientCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_agent_client_counter");

    private static readonly Counter<int> AgentClientVersionCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>("zeeq_agent_client_version_counter");

    /// <summary>
    /// If this header is present, beta tools (marked wth "(BETA)") are included
    /// </summary>
    private const string BetaHeaderName = "x-zeeq-enable-beta";

    /// <summary>
    /// This header dynamically controls the memory tools mode.
    /// - none: Memory tools are not included in the tool list.
    /// - read: Read only access to memory
    /// - write: Read and write, but no reinforcement;
    /// - all: Read, write, and reinforce; default if not specified
    /// </summary>
    private const string MemoryModeHeaderName = "x-zeeq-memory-mode";

    extension(IMcpServerBuilder server)
    {
        /// <summary>
        /// Configures custom MCP filters for the server.
        /// </summary>
        /// <returns>The MCP server builder with filters applied.</returns>
        public IMcpServerBuilder WithZeeqMcpFilters()
        {
            server.WithMessageFilters(filters =>
            {
                // Filter for sending notification of resource updates on startup.
                // TODO: Send notifications for each resource.
                filters.AddIncomingFilter(next =>
                    async (context, cancellationToken) =>
                    {
                        if (
                            context.JsonRpcMessage is JsonRpcRequest request
                            && request.Method == "initialize"
                        )
                        {
                            await context.Server.SendNotificationAsync(
                                NotificationMethods.ResourceListChangedNotification,
                                new ResourceListChangedNotificationParams(),
                                cancellationToken: cancellationToken
                            );
                        }

                        await next(context, cancellationToken);
                    }
                );
                // Standard trace and exception tracking.
                filters.AddIncomingFilter(next =>
                    async (context, cancellationToken) =>
                    {
                        if (context.Server.ClientInfo != null)
                        {
                            var clientVersion =
                                $"{context.Server.ClientInfo.Name}-{context.Server.ClientInfo.Version}";

                            ZeeqTelemetry.SetTags([
                                ("mcp.client_name", context.Server.ClientInfo.Name),
                                ("mcp.client_version", clientVersion),
                            ]);

                            AgentClientCounter.Increment(
                                tags: [("client_name", context.Server.ClientInfo.Name)]
                            );

                            AgentClientVersionCounter.Increment(
                                tags: [("client_version", clientVersion)]
                            );
                        }

                        var toolName = string.Empty;

                        if (context.JsonRpcMessage is JsonRpcRequest request)
                        {
                            toolName = request.Params?["name"]?.GetValue<string>();
                        }

                        var userAgent = "unspecified";

                        var httpContext = context
                            .Services?.GetService<IHttpContextAccessor>()
                            ?.HttpContext;

                        var user = httpContext?.User?.AuthenticatedUser();

                        if (httpContext != null && !string.IsNullOrWhiteSpace(toolName))
                        {
                            // Set attributes from the HTTP context and emit telemetry
                            // If this is a domain tool call (vs base MCP protocol message).
                            userAgent =
                                httpContext.Request.Headers.UserAgent.FirstOrDefault()
                                ?? "unspecified";

                            ZeeqTelemetry.SetTags([
                                ("http.user_agent", userAgent),
                                ("user", user?.Email ?? "unknown-user"),
                            ]);

                            // organization_id/team_id let the metrics pipeline persist these
                            // counters per organization (the capture rule requires organization_id).
                            // Absent when unauthenticated — emitted without the tag, so OTEL is
                            // unchanged and the pipeline simply skips persistence.
                            var organizationId = httpContext.User.FindFirstValue(
                                AuthClaims.OrganizationId
                            );
                            var teamId = httpContext.User.FindFirstValue(AuthClaims.TeamId);
                            var userEmail = user?.Email ?? "unknown-user";

                            List<(string Key, object? Value)> userAgentTags =
                            [
                                ("user_agent", userAgent),
                                ("user", userEmail),
                            ];
                            List<(string Key, object? Value)> toolCallTags =
                            [
                                ("tool_name", toolName),
                                ("user_agent", userAgent),
                                ("user", userEmail),
                            ];
                            if (!string.IsNullOrEmpty(organizationId))
                            {
                                userAgentTags.Add(("organization_id", (object)organizationId));
                                toolCallTags.Add(("organization_id", (object)organizationId));
                            }
                            if (!string.IsNullOrEmpty(teamId))
                            {
                                userAgentTags.Add(("team_id", (object)teamId));
                                toolCallTags.Add(("team_id", (object)teamId));
                            }

                            UserAgentCounter.Increment(tags: [.. userAgentTags]);

                            // Only emit for actual domain tool calls; this path also gets activated
                            // for non-tool messages (vs base MCP protocol messages).
                            ToolCallCounter.Increment(tags: [.. toolCallTags]);
                        }

                        try
                        {
                            await next(context, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            var requestParams = context.JsonRpcMessage
                                is JsonRpcRequest failedRequest
                                ? failedRequest.Params?.ToJsonString()
                                : string.Empty;

                            Activity.Current?.AddException(
                                ex,
                                tags:
                                [
                                    new("exception_message", ex.Message),
                                    new("user_agent", userAgent),
                                    new("tool_name", toolName),
                                    new("request_params", requestParams),
                                ]
                            );

                            await context.Server.SendMessageAsync(
                                new JsonRpcError
                                {
                                    Error = new JsonRpcErrorDetail
                                    {
                                        Code = -1,
                                        Message = ex.Message,
                                    },
                                },
                                cancellationToken
                            );

                            return;
                        }
                    }
                );
            });

            // TODO: Implement user-level tool filter selection
            server.WithListToolsHandler(
                async (context, cancellationToken) =>
                {
                    // Resolve HttpContext via DI
                    var httpContext = context
                        .Services?.GetRequiredService<IHttpContextAccessor>()
                        .HttpContext;

                    // NOTE: ToolCollection here is per-request, NOT a shared singleton.
                    // In stateless HTTP mode (options.Stateless = true, see SetupMcpExtensions.cs),
                    // the MCP SDK creates a fresh McpServerOptions via IOptionsFactory<>.Create()
                    // for every request and re-populates ToolCollection from the registered
                    // McpServerTool DI singletons. Mutating it below (.Remove) only affects the
                    // current request, so it cannot strip tools from other concurrent or future
                    // clients. This would be a shared-state bug in stateful mode, but this app
                    // runs stateless. See MCP SDK StreamableHttpHandler + McpServerOptionsSetup.
                    var toolCollection = context.Server?.ServerOptions?.ToolCollection;

                    // Check for the beta header
                    bool betaEnabled =
                        httpContext?.Request.Headers.ContainsKey("x-zeeq-enable-beta") ?? false;

                    if (!betaEnabled)
                    {
                        var betaTools =
                            toolCollection?.Where(tool =>
                                tool.ProtocolTool.Title?.Contains("(BETA)") ?? false
                            )
                            ?? [];

                        foreach (var tool in betaTools.ToList())
                        {
                            toolCollection?.Remove(tool);
                        }
                    }

                    // Filter out memory tools.
                    var memoryToolsMode =
                        httpContext
                            ?.Request.Headers[MemoryModeHeaderName]
                            .FirstOrDefault()
                            ?.ToLowerInvariant()
                        ?? "all";

                    Func<McpServerTool, bool> toolFilter = memoryToolsMode switch
                    {
                        "none" => tool => tool.ProtocolTool.Name.EndsWith("_memory"),
                        "read" => tool =>
                            tool.ProtocolTool.Name.EndsWith("_memory")
                            && tool.ProtocolTool.Name != "recall_memory",
                        "write" => tool =>
                            tool.ProtocolTool.Name.EndsWith("_memory")
                            && tool.ProtocolTool.Name != "recall_memory"
                            && tool.ProtocolTool.Name != "store_memory",
                        "all" => tool => false, // Do nothing; all tools are included.
                        _ => tool => false, // Unrecognized value; default to including all tools.
                    };

                    context?.Server?.ServerOptions?.ToolCollection?.Remove(toolFilter);

                    // NOTE: Same per-request rationale as above — safe to mutate here because
                    // ToolCollection is recreated per request in stateless mode.

                    // Forced to return this as the handler expects it; these are tools to ADD to the list.
                    return new ListToolsResult
                    {
                        Tools = [], // Empty; these are additional tools.
                    };
                }
            );

            // Add custom filters here
            return server;
        }
    }
}

file static class Extensions
{
    extension(McpServerPrimitiveCollection<McpServerTool> tools)
    {
        /// <summary>
        /// Removes tools from the collection based on a predicate.
        /// </summary>
        /// <param name="predicate">
        /// A function that takes an <see cref="McpServerTool"/> and returns
        /// <see langword="true"/> if it should be removed.
        /// </param>
        public void Remove(Func<McpServerTool, bool> predicate)
        {
            var toRemove = tools.Where(predicate).ToList();

            foreach (var tool in toRemove)
            {
                tools.Remove(tool);
            }
        }
    }
}
