using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Carts;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Carts;

/// <summary>
/// MCP tools for saved findings carts.  Tools here surface the same compiled instructions
/// as the HTTP <c>GET /carts/{id}/text</c> endpoint, using the shared
/// `CartInstructionsTextBuilder`.
/// </summary>
/// <remarks>
/// <b>Org-scoped lookup.</b>  MCP tools look up carts by organization + cart id — not by
/// owner.  This is deliberate: the cart id is a shared capability the user copies out of
/// the UI, and the local agent's MCP session may carry a different <c>sub</c> than the
/// browser session that saved the cart.  See <see cref="ICartStore.FindAsync"/>.
/// </remarks>
[McpServerToolType, Description("Provides Zeeq findings-cart MCP tools.")]
public sealed partial class CartMcpTools
{
    private static readonly Counter<int> GetCartFindingsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_get_cart_findings_total",
            "The total number of times the get_cart_findings MCP tool is called."
        );

    /// <summary>
    /// Records a structured tool-call outcome without leaking finding body content into
    /// telemetry. The returned <paramref name="response"/> is passed through unchanged so
    /// callers can inline this in a return expression.
    /// </summary>
    private static string RecordToolCall(
        Counter<int> counter,
        ClaimsPrincipal? user,
        string result,
        string cartId,
        string response
    )
    {
        ZeeqTelemetry.SetTags([
            ("result", result),
            ("user", user?.AuthenticatedUser()?.Sub ?? "unknown"),
            ("cart_id", cartId),
        ]);

        return response;
    }
}
