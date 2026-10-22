using System.Net;
using System.Text.Json;

namespace Zeeq.Core.Identity.Tests;

/// <summary>Checks the local 0factor OpenID Connect boundary with its imported realm.</summary>
/// <remarks>
/// Run after the local Aspire stack has imported the realm:
/// <c>dotnet run --project src/backend/Zeeq.Core.Identity.Tests -- --output detailed --disable-logo --treenode-filter "/*/*/ZeroFactorProtocolIntegrationTests/*"</c>.
/// Requests are restricted to localhost:9321; redirects, proxies, and cookies are disabled.
/// </remarks>
[Explicit]
[Property("integration", "0factor-e2e")]
public sealed class ZeroFactorProtocolIntegrationTests
{
    private const string Issuer = ZeroFactorProtocolClient.Issuer;
    private const string ClientId = ZeroFactorProtocolClient.ClientId;
    private const string ChallengeMethod = ZeroFactorProtocolClient.ChallengeMethod;

    // NOTE: Independent expected values keep the approved callback contract under test.
    private static readonly string[] Callbacks =
    [
        "http://zeeq-web.localhost:8095/auth/callback/mock",
        "http://localhost:8096/auth/callback/mock",
    ];

    /// <summary>Uses stable synthetic subject IDs from the copied realm fixture as safe TUnit case names.</summary>
    public static IEnumerable<string> EnabledPersonaCases()
    {
        var subjects = ZeroFactorProtocolClient
            .LoadRealm()
            .Users.Where(user => user.Enabled)
            .Select(user => user.Id)
            .ToArray();
        return subjects.Length > 0
            ? subjects
            : throw new ZeroFactorProtocolFailure(
                "The copied 0factor realm fixture has no enabled personas."
            );
    }

    [Test]
    public async Task Discovery_AdvertisesExactIssuerS256AndJwks()
    {
        await WithMetadata(
            "Discovery validation could not complete.",
            async (_, metadata) =>
            {
                await Check(metadata.Issuer == Issuer, "Discovery issuer did not match the realm.");
                await Check(
                    metadata.ChallengeMethods.Contains(ChallengeMethod, StringComparer.Ordinal),
                    "Discovery did not advertise S256."
                );
                await Check(
                    metadata.Keys.RootElement.GetProperty("keys").EnumerateArray().Any(),
                    "The JWKS endpoint exposed no signing keys."
                );
            }
        );
    }

    [Test]
    [Arguments("unknown-client")]
    [Arguments("unlisted-redirect")]
    public async Task Authorization_StrictClient_RejectsUnknownClientAndRedirect(string scenario)
    {
        await WithMetadata(
            "Strict authorization rejection could not complete.",
            async (client, metadata) =>
            {
                var request = client.NewAuthorization(Callbacks[0]);
                if (scenario == "unknown-client")
                {
                    request.Parameters["client_id"] = "0factor-unregistered-client";
                }
                else
                {
                    request.Parameters["redirect_uri"] = "http://localhost:9999/not-registered";
                }

                var response = await client.GetAsync(
                    client.BuildQuery(metadata.AuthorizationEndpoint, request.Parameters)
                );
                await Check(
                    response.Status == HttpStatusCode.BadRequest && response.Location is null,
                    "The provider accepted an unknown client or unregistered redirect."
                );
            }
        );
    }

    [Test]
    [Arguments("missing-challenge")]
    [Arguments("missing-method")]
    [Arguments("plain-method")]
    public async Task Authorization_S256Client_RejectsInvalidPkceWithStateAndIssuer(string scenario)
    {
        await WithMetadata(
            "PKCE rejection could not complete.",
            async (client, metadata) =>
            {
                var request = client.NewAuthorization(Callbacks[0]);
                switch (scenario)
                {
                    case "missing-challenge":
                        request.Parameters.Remove("code_challenge");
                        request.Parameters.Remove("code_challenge_method");
                        break;
                    case "missing-method":
                        request.Parameters.Remove("code_challenge_method");
                        break;
                    case "plain-method":
                        request.Parameters["code_challenge_method"] = "plain";
                        break;
                    default:
                        throw new ZeroFactorProtocolFailure("The PKCE test input was unsupported.");
                }

                var response = await client.GetAsync(
                    client.BuildQuery(metadata.AuthorizationEndpoint, request.Parameters)
                );
                await Check(
                    response.Status == HttpStatusCode.Redirect && response.Location is not null,
                    "The invalid PKCE request did not return a safe error redirect."
                );

                var location = client.ResolveCallback(
                    response,
                    metadata.AuthorizationEndpoint,
                    Callbacks[0]
                );
                var query = ZeroFactorProtocolClient.ParseQuery(location.Query);
                await Check(
                    ZeroFactorProtocolClient.IsCallback(location, Callbacks[0]),
                    "The error redirect used an unregistered callback."
                );
                await Check(
                    ZeroFactorProtocolClient.Value(query, "error") == "invalid_request"
                        && !query.ContainsKey("code"),
                    "The invalid PKCE request did not fail without issuing a code."
                );
                await Check(
                    string.Equals(
                        ZeroFactorProtocolClient.Value(query, "state"),
                        request.State,
                        StringComparison.Ordinal
                    ),
                    "The PKCE error redirect did not preserve state."
                );
                await Check(
                    string.Equals(
                        ZeroFactorProtocolClient.Value(query, "iss"),
                        metadata.Issuer,
                        StringComparison.Ordinal
                    ),
                    "The PKCE error redirect did not identify the issuer."
                );
            }
        );
    }

    [Test]
    [MethodDataSource(nameof(EnabledPersonaCases))]
    public async Task AuthorizationCode_EnabledPersona_ValidatesIdentityUserInfoAndReplay(
        string personaSubject
    )
    {
        await WithMetadata(
            "Fixture persona authorization-code flow could not complete.",
            async (client, metadata) =>
            {
                var persona = client.Realm.Users.Single(user => user.Id == personaSubject);
                var authorization = await client.AuthorizeAsync(metadata, persona, Callbacks[0]);
                var tokens = await client.RedeemAsync(metadata, authorization);
                var identity = ZeroFactorProtocolTokenVerifier.VerifyIdToken(
                    tokens.IdToken,
                    persona,
                    authorization.Nonce,
                    metadata,
                    ClientId
                );

                using var userInfo = await client.GetJsonAsync(
                    metadata.UserInfoEndpoint,
                    tokens.AccessToken
                );
                await Check(
                    string.Equals(
                        ZeroFactorProtocolClient.ReadString(userInfo.RootElement, "sub"),
                        identity.Subject,
                        StringComparison.Ordinal
                    )
                        && string.Equals(
                            ZeroFactorProtocolClient.ReadString(userInfo.RootElement, "email"),
                            identity.Email,
                            StringComparison.Ordinal
                        ),
                    "JSON userinfo did not match the signed identity."
                );

                var replay = await client.RedeemResponseAsync(
                    metadata,
                    authorization.Code,
                    authorization.Verifier,
                    authorization.Callback
                );
                await CheckRejectedGrant(
                    replay,
                    "A redeemed authorization code was accepted again."
                );
            }
        );
    }

    [Test]
    public async Task AuthorizationCode_AllRegisteredCallbacks_CompletePublicPkceExchange()
    {
        await WithMetadata(
            "Registered callback exchange could not complete.",
            async (client, metadata) =>
            {
                var persona = client.Realm.Users.First(user => user.Enabled);
                foreach (var callback in Callbacks)
                {
                    var authorization = await client.AuthorizeAsync(metadata, persona, callback);
                    var tokens = await client.RedeemAsync(metadata, authorization);
                    await Check(
                        tokens.AccessToken.Length > 0 && tokens.IdToken.Length > 0,
                        "A registered callback did not complete public PKCE exchange."
                    );
                }
            }
        );
    }

    [Test]
    public async Task AuthorizationCode_WrongVerifierFailsAndConsumesCode()
    {
        await WithMetadata(
            "Wrong-verifier code-consumption check could not complete.",
            async (client, metadata) =>
            {
                var persona = client.Realm.Users.First(user => user.Enabled);
                var authorization = await client.AuthorizeAsync(metadata, persona, Callbacks[0]);
                var wrongVerifier = ZeroFactorProtocolClient.NewVerifier();
                while (wrongVerifier == authorization.Verifier)
                {
                    wrongVerifier = ZeroFactorProtocolClient.NewVerifier();
                }

                var wrong = await client.RedeemResponseAsync(
                    metadata,
                    authorization.Code,
                    wrongVerifier,
                    authorization.Callback
                );
                await CheckRejectedGrant(wrong, "A wrong PKCE verifier was accepted.");

                var retry = await client.RedeemResponseAsync(
                    metadata,
                    authorization.Code,
                    authorization.Verifier,
                    authorization.Callback
                );
                await CheckRejectedGrant(
                    retry,
                    "The code remained usable after a failed verifier attempt."
                );
            }
        );
    }

    private static async Task WithMetadata(
        string safeFailure,
        Func<ZeroFactorProtocolClient, ZeroFactorProtocolMetadata, Task> test
    )
    {
        using var client = new ZeroFactorProtocolClient();
        try
        {
            await Check(
                client.Realm.Realm == "zeeq" && client.Realm.Enabled && !client.Realm.Lenient,
                "The realm fixture must be enabled, strict, and named zeeq."
            );
            var configuredClient = client.ConfiguredClient;
            await Check(
                configuredClient.PublicClient
                    && configuredClient.RedirectUris.SequenceEqual(
                        Callbacks,
                        StringComparer.Ordinal
                    )
                    && configuredClient.Attributes.TryGetValue(
                        "pkce.code.challenge.method",
                        out var method
                    )
                    && method == ChallengeMethod,
                "The realm client must be public, S256-only, and use both approved callbacks."
            );
            using var metadata = await client.LoadMetadataAsync();
            await test(client, metadata);
        }
        catch (ZeroFactorProtocolFailure)
        {
            throw;
        }
        catch
        {
            throw new ZeroFactorProtocolFailure(safeFailure);
        }
    }

    private static async Task Check(bool condition, string safeDiagnostic)
    {
        try
        {
            // NOTE: Keep assertion internals suppressed so provider values cannot leak.
            await Assert.That(condition).IsTrue();
        }
        catch
        {
            throw new ZeroFactorProtocolFailure(safeDiagnostic);
        }
    }

    private static async Task CheckRejectedGrant(
        ZeroFactorProtocolResponse response,
        string diagnostic
    )
    {
        await Check(
            response.Status == HttpStatusCode.BadRequest
                && string.Equals(
                    response.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase
                ),
            diagnostic
        );
        using var body = ZeroFactorProtocolClient.ParseJson(response.Body);
        await Check(
            ZeroFactorProtocolClient.ReadString(body.RootElement, "error") == "invalid_grant"
                && !body.RootElement.TryGetProperty("access_token", out _)
                && !body.RootElement.TryGetProperty("id_token", out _),
            diagnostic
        );
    }
}
