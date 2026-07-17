using System.ComponentModel;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Zeeq.Core.Carts;
using Zeeq.Core.Identity;
using Zeeq.Platform.Carts;

namespace Zeeq.Mcp.Carts;

public sealed partial class CartMcpTools
{
    [GeneratedRegex(CartLimits.CartIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex CartIdRegex();

    /// <summary>
    /// Retrieves a saved findings cart by id.  Returns the compiled XML instructions
    /// block — the same format produced by the browser "Copy" button.  Unsaved drafts
    /// are invisible here by construction: only rows that were persisted via
    /// <c>POST /carts</c> can be found.
    /// </summary>
    [McpServerTool(Name = "get_cart_findings", Title = "Get Cart Findings")]
    [Description(
        """
            A human reviewer has created a saved cart of reviewer findings that needs to be fixed.
            This tool retrieves the full cart of findings that should be verified and addressed.

            <get_cart_findings.triggers>
            - The user pasted instructions containing a cart ID and asked you to act on it
            - The user presents an ID that is a 6 letter adjective, 4 letter noun, and 10 character nanoid (e.g. snappy-lake-a1b2c3d4e5)
            - You need the full body of the code reviewer findings in order to verify and fix them
            </get_cart_findings.triggers>

            Use this tool to fetch the full cart of findings.
            Work with your local user to assess each finding and decide on a course of action.
            Process each finding, one-by-one and present a sensible default along with other options for verified fixes.
            """
    )]
    public static async Task<string> GetCartFindings(
        ICartStore store,
        ClaimsPrincipal? user,
        [Description("The cart id from the copied instructions.")] string cartId,
        CancellationToken cancellationToken = default
    )
    {
        if (!CartIdRegex().IsMatch(cartId))
        {
            return RecordToolCall(
                GetCartFindingsCounter,
                user,
                "invalid_cart_id",
                cartId,
                $"Cart id '{cartId}' does not match the expected format (e.g. snappy-lake-a1b2c3d4e5)."
            );
        }

        var organizationId = user?.AsZeeqMinimalIdentity().OrganizationId;

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return RecordToolCall(
                GetCartFindingsCounter,
                user,
                "missing_org",
                cartId,
                "Active organization is required."
            );
        }

        var cart = await store.FindAsync(organizationId, cartId, cancellationToken);

        if (cart is null)
        {
            return RecordToolCall(
                GetCartFindingsCounter,
                user,
                "not_found",
                cartId,
                $"Cart '{cartId}' was not found. It may not have been saved yet."
            );
        }

        var text = cart.ItemsPayload.ToAgentInstructions();

        return RecordToolCall(GetCartFindingsCounter, user, "success", cartId, text);
    }
}
