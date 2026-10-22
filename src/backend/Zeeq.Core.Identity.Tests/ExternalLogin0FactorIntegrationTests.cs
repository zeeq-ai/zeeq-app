using System.Net;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Public login-start contract check against a running local Zeeq + 0factor stack.
/// </summary>
/// <remarks>
/// Run explicitly after the Aspire graph is running with the imported 0factor realm:
/// <c>dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/ExternalLogin0FactorIntegrationTests/*"</c>.
/// Set <c>ZEEQ_E2E_BASE_URL</c> to override the local web origin. The test never
/// follows the IdP redirect or writes its state, verifier, or authorization URL to output.
/// </remarks>
[Explicit]
[Property("integration", "0factor-e2e")]
public sealed class ExternalLogin0FactorIntegrationTests
{
    private static readonly HashSet<string> ApprovedCallbacks =
    [
        "http://zeeq-web.localhost:8095/auth/callback/mock",
        "http://localhost:8096/auth/callback/mock",
    ];

    [Test]
    public async Task LoginStart_UsesRealmClientAllowedCallbackAndS256State()
    {
        var baseUrl =
            Environment.GetEnvironmentVariable("ZEEQ_E2E_BASE_URL")
            ?? "http://zeeq-web.localhost:8095";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("ZEEQ_E2E_BASE_URL must be an absolute URL.");
        }

        var isLocalOrigin =
            baseUri.IsLoopback
            || baseUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        if (
            baseUri.Scheme is not ("http" or "https")
            || !isLocalOrigin
            || !string.IsNullOrEmpty(baseUri.UserInfo)
        )
        {
            throw new InvalidOperationException(
                "ZEEQ_E2E_BASE_URL must be an HTTP(S) loopback or .localhost origin."
            );
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var loginResponse = await client.GetAsync(new Uri(baseUri, "/auth/login/mock"));
        var redirect = loginResponse.Headers.Location;
        if (loginResponse.StatusCode != HttpStatusCode.Redirect || redirect is null)
        {
            throw new InvalidOperationException(
                "The public mock login route did not return an IdP redirect."
            );
        }

        var parameters = ParseQuery(redirect.Query);
        var callbackIsAllowed =
            parameters.TryGetValue("redirect_uri", out var callback)
            && ApprovedCallbacks.Contains(callback);
        var scopesAreExpected =
            parameters.TryGetValue("scope", out var scopes)
            && string.Join(' ', scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order())
                == "email openid profile";

        await Assert.That(redirect.Scheme).IsEqualTo("http");
        await Assert.That(redirect.Host).IsEqualTo("localhost");
        await Assert.That(redirect.Port).IsEqualTo(9321);
        await Assert
            .That(redirect.AbsolutePath == "/realms/zeeq/protocol/openid-connect/auth")
            .IsTrue();
        await Assert
            .That(parameters.GetValueOrDefault("client_id") == "mcp-openiddict-local")
            .IsTrue();
        await Assert.That(callbackIsAllowed).IsTrue();
        await Assert.That(parameters.GetValueOrDefault("response_type") == "code").IsTrue();
        await Assert.That(scopesAreExpected).IsTrue();
        await Assert.That(parameters.GetValueOrDefault("code_challenge_method") == "S256").IsTrue();
        await Assert.That(parameters.GetValueOrDefault("state") is { Length: 32 }).IsTrue();
        await Assert
            .That(parameters.GetValueOrDefault("code_challenge") is { Length: 43 })
            .IsTrue();
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal
            );
}
