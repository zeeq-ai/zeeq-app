using Zeeq.Core.Common;
using Zeeq.Core.Identity;

namespace Zeeq.Core.Identity.Tests;

public class DynamicClientRegistrationTests
{
    private static readonly DynamicClientRegistrationSettings Settings = new();

    [Test]
    public async Task Validate_WithLoopbackRedirect_AllowsNativePublicClient()
    {
        var request = new DynamicClientRegistrationRequest(
            ClientName: "Local MCP Test",
            RedirectUris: ["http://127.0.0.1:3456/callback"],
            GrantTypes: ["authorization_code", "refresh_token"],
            ResponseTypes: ["code"],
            Scope: "openid profile email mcp:tools",
            ClientUri: "http://localhost:3456",
            TokenEndpointAuthMethod: "none",
            ApplicationType: "native"
        );

        var error = DynamicClientRegistrationValidator.Validate(request, Settings);

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Validate_WithFragmentRedirect_RejectsClient()
    {
        var request = new DynamicClientRegistrationRequest(
            ClientName: "Local MCP Test",
            RedirectUris: ["http://127.0.0.1:3456/callback#fragment"],
            GrantTypes: ["authorization_code"],
            ResponseTypes: ["code"],
            Scope: "mcp:tools",
            ClientUri: "http://localhost:3456",
            TokenEndpointAuthMethod: "none",
            ApplicationType: "native"
        );

        var error = DynamicClientRegistrationValidator.Validate(request, Settings);

        await Assert.That(error?.Error).IsEqualTo("invalid_redirect_uri");
    }

    [Test]
    public async Task Validate_WithClientSecretBasicAuthMethod_RejectsClient()
    {
        var request = new DynamicClientRegistrationRequest(
            ClientName: "Local MCP Test",
            RedirectUris: ["http://localhost:3456/callback"],
            GrantTypes: ["authorization_code"],
            ResponseTypes: ["code"],
            Scope: "mcp:tools",
            ClientUri: "http://localhost:3456",
            TokenEndpointAuthMethod: "client_secret_basic",
            ApplicationType: "native"
        );

        var error = DynamicClientRegistrationValidator.Validate(request, Settings);

        await Assert.That(error?.Error).IsEqualTo("invalid_client_metadata");
    }

    [Test]
    public async Task Validate_WithClientSecretPostAuthMethod_RejectsClient()
    {
        var request = new DynamicClientRegistrationRequest(
            ClientName: "Local MCP Test",
            RedirectUris: ["http://localhost:3456/callback"],
            GrantTypes: ["authorization_code"],
            ResponseTypes: ["code"],
            Scope: "mcp:tools",
            ClientUri: "http://localhost:3456",
            TokenEndpointAuthMethod: "client_secret_post",
            ApplicationType: "native"
        );

        var error = DynamicClientRegistrationValidator.Validate(request, Settings);

        await Assert.That(error?.Error).IsEqualTo("invalid_client_metadata");
    }

    [Test]
    public async Task NormalizeScopes_AddsMcpToolsWhenMissing()
    {
        var scopes = DynamicClientRegistrationValidator.NormalizeScopes("openid email");

        await Assert.That(scopes).IsEquivalentTo(["openid", "email", "mcp:tools"]);
    }
}
