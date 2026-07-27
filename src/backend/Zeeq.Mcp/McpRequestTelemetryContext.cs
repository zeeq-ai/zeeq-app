using Microsoft.AspNetCore.Http;

namespace Zeeq.Mcp;

/// <summary>
/// Request-scoped MCP client metadata captured once at the message-filter boundary.
/// </summary>
/// <remarks>
/// Dynamic prompts run through MCP prompt handlers rather than normal document tools. The generic
/// MCP filter is still the best place to normalize the transport/client identity because it sees
/// the raw HTTP headers, Codex metadata fallback, and SDK client info. Storing this compact value in
/// <see cref="HttpContext.Items" /> lets later services add the same client dimensions to business
/// metrics without re-parsing the request.
/// </remarks>
internal sealed record McpRequestTelemetryContext(
    string UserAgent,
    string? ClientName,
    string? ClientVersion
)
{
    /// <summary>Opaque key used to store the context on <see cref="HttpContext.Items" />.</summary>
    public static readonly object HttpContextItemKey = new();

    /// <summary>Reads the context stored by the MCP message filter for the current request.</summary>
    public static McpRequestTelemetryContext? From(HttpContext? httpContext) =>
        httpContext?.Items.TryGetValue(HttpContextItemKey, out var value) == true
            ? value as McpRequestTelemetryContext
            : null;
}
