using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zeeq.Core.Identity.Tests;

internal sealed record ZeroFactorAuthorizationRequest(
    Dictionary<string, string> Parameters,
    string Verifier,
    string State,
    string Nonce
)
{
    public override string ToString() => "authorization request";
}

internal sealed record ZeroFactorAuthorizationGrant(
    string Code,
    string Verifier,
    string Nonce,
    string Callback
)
{
    public override string ToString() => "authorization grant";
}

internal sealed record ZeroFactorTokenGrant(string AccessToken, string IdToken)
{
    public override string ToString() => "token grant";
}

internal sealed record ZeroFactorProtocolResponse(
    HttpStatusCode Status,
    string? MediaType,
    Uri? Location,
    string Body
)
{
    public override string ToString() => "provider response";
}

internal sealed record ZeroFactorProtocolMetadata(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri UserInfoEndpoint,
    string[] ChallengeMethods,
    JsonDocument Keys
) : IDisposable
{
    public void Dispose() => Keys.Dispose();
}

internal sealed class ZeroFactorProtocolFailure(string message) : Exception(message) { }

internal sealed class ZeroFactorRealmFixture
{
    public string Realm { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public bool Lenient { get; init; } = true;
    public ZeroFactorRealmClient[] Clients { get; init; } = [];
    public ZeroFactorRealmUser[] Users { get; init; } = [];

    public static ZeroFactorRealmFixture Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "0factor", "zeeq-realm.json");
            return JsonSerializer.Deserialize<ZeroFactorRealmFixture>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new ZeroFactorProtocolFailure(
                "The copied 0factor realm fixture could not be loaded."
            );
        }
    }
}

internal sealed class ZeroFactorRealmClient
{
    public string ClientId { get; init; } = string.Empty;
    public bool PublicClient { get; init; }
    public string[] RedirectUris { get; init; } = [];
    public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class ZeroFactorRealmUser
{
    public string Id { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public bool EmailVerified { get; init; } = true;
}

internal sealed class ZeroFactorProtocolClient : IDisposable
{
    internal const string Issuer = "http://localhost:9321/realms/zeeq";
    internal const string ClientId = "mcp-openiddict-local";
    internal const string ChallengeMethod = "S256";

    internal static ZeroFactorRealmFixture LoadRealm() => ZeroFactorRealmFixture.Load();

    private readonly HttpClient _http = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    internal ZeroFactorRealmFixture Realm { get; } = LoadRealm();
    internal ZeroFactorRealmClient ConfiguredClient =>
        Realm.Clients.Single(client => client.ClientId == ClientId);

    private static Uri IssuerDirectory => new(Issuer + "/", UriKind.Absolute);

    internal async Task<ZeroFactorProtocolMetadata> LoadMetadataAsync()
    {
        Require(
            Realm.Realm == "zeeq" && !Realm.Lenient,
            "The realm fixture must be strict and named zeeq."
        );

        using var discovery = await GetJsonAsync(
            new Uri(IssuerDirectory, ".well-known/openid-configuration")
        );
        var root = discovery.RootElement;
        var issuer = ReadString(root, "issuer");
        var methods = root.GetProperty("code_challenge_methods_supported")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        Require(issuer == Issuer, "Discovery issuer did not match the expected issuer.");
        Require(
            methods.Contains(ChallengeMethod, StringComparer.Ordinal),
            "Discovery omitted S256."
        );

        var signingKeys = await GetJsonAsync(ReadEndpoint(root, "jwks_uri"));
        return new ZeroFactorProtocolMetadata(
            issuer,
            ReadEndpoint(root, "authorization_endpoint"),
            ReadEndpoint(root, "token_endpoint"),
            ReadEndpoint(root, "userinfo_endpoint"),
            methods,
            signingKeys
        );
    }

    internal async Task<ZeroFactorAuthorizationGrant> AuthorizeAsync(
        ZeroFactorProtocolMetadata metadata,
        ZeroFactorRealmUser persona,
        string callback
    )
    {
        var request = NewAuthorization(callback);
        var requestUri = BuildQuery(metadata.AuthorizationEndpoint, request.Parameters);
        var page = await GetAsync(requestUri);
        Require(page.Status == HttpStatusCode.OK, "The provider did not render its login form.");

        var (action, auth) = ParseLoginForm(page.Body);
        Require(
            !string.IsNullOrWhiteSpace(action) && !string.IsNullOrWhiteSpace(auth),
            "The login form was incomplete."
        );
        var actionUri = new Uri(IssuerDirectory, action!);
        var login = await PostFormAsync(
            actionUri,
            new Dictionary<string, string>
            {
                ["auth"] = auth!,
                ["email"] = string.IsNullOrWhiteSpace(persona.Email)
                    ? persona.Username
                    : persona.Email,
            }
        );
        Require(
            login.Status == HttpStatusCode.Redirect && login.Location is not null,
            "Fixture persona sign-in did not return a registered callback."
        );

        var redirect = ResolveCallback(login, actionUri, callback);
        var query = ParseQuery(redirect.Query);
        var code = Value(query, "code");
        Require(
            string.Equals(Value(query, "state"), request.State, StringComparison.Ordinal),
            "The authorization response did not preserve state."
        );
        Require(
            string.Equals(Value(query, "iss"), metadata.Issuer, StringComparison.Ordinal),
            "The authorization response issuer did not match discovery."
        );
        Require(
            !string.IsNullOrWhiteSpace(code),
            "The provider did not issue an authorization code."
        );
        return new ZeroFactorAuthorizationGrant(code!, request.Verifier, request.Nonce, callback);
    }

    internal async Task<ZeroFactorTokenGrant> RedeemAsync(
        ZeroFactorProtocolMetadata metadata,
        ZeroFactorAuthorizationGrant grant
    )
    {
        var response = await RedeemResponseAsync(
            metadata,
            grant.Code,
            grant.Verifier,
            grant.Callback
        );
        Require(
            response.Status == HttpStatusCode.OK
                && string.Equals(
                    response.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase
                ),
            "The public-client PKCE exchange did not return JSON HTTP 200."
        );
        using var body = ParseJson(response.Body);
        var root = body.RootElement;
        var accessToken = ReadString(root, "access_token");
        var idToken = ReadString(root, "id_token");
        Require(
            ReadString(root, "token_type") == "Bearer"
                && accessToken.Length > 0
                && idToken.Length > 0,
            "The token response did not contain a bearer access token and ID token."
        );
        return new ZeroFactorTokenGrant(accessToken, idToken);
    }

    internal Task<ZeroFactorProtocolResponse> RedeemResponseAsync(
        ZeroFactorProtocolMetadata metadata,
        string code,
        string verifier,
        string callback
    ) =>
        PostFormAsync(
            metadata.TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["client_id"] = ClientId,
                ["redirect_uri"] = callback,
            },
            acceptJson: true
        );

    internal async Task<JsonDocument> GetJsonAsync(Uri uri, string? accessToken = null)
    {
        var response = await GetAsync(uri, accessToken);
        Require(
            response.Status == HttpStatusCode.OK
                && string.Equals(
                    response.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase
                ),
            "A JSON provider endpoint did not return HTTP 200."
        );
        return ParseJson(response.Body);
    }

    internal Task<ZeroFactorProtocolResponse> GetAsync(Uri uri, string? bearerToken = null) =>
        SendAsync(HttpMethod.Get, uri, null, bearerToken);

    internal Task<ZeroFactorProtocolResponse> PostFormAsync(
        Uri uri,
        Dictionary<string, string> fields,
        bool acceptJson = false
    ) => SendAsync(HttpMethod.Post, uri, fields, null, acceptJson);

    internal Uri ResolveCallback(
        ZeroFactorProtocolResponse response,
        Uri baseUri,
        string expectedCallback
    )
    {
        var location =
            response.Location is null
                ? throw new ZeroFactorProtocolFailure(
                    "The provider response did not include a redirect."
                )
            : response.Location.IsAbsoluteUri ? response.Location
            : new Uri(baseUri, response.Location);
        Require(
            IsCallback(location, expectedCallback),
            "The provider returned an unexpected callback."
        );
        return location;
    }

    private async Task<ZeroFactorProtocolResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        Dictionary<string, string>? fields,
        string? bearerToken,
        bool acceptJson = false
    )
    {
        if (
            uri.Scheme != Uri.UriSchemeHttp
            || uri.Host != "localhost"
            || uri.Port != 9321
            || !string.IsNullOrEmpty(uri.UserInfo)
        )
        {
            throw new ZeroFactorProtocolFailure("A provider request was outside localhost:9321.");
        }

        try
        {
            using var request = new HttpRequestMessage(method, uri);
            if (fields is not null)
            {
                request.Content = new FormUrlEncodedContent(fields);
            }
            if (bearerToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    bearerToken
                );
            }
            if (acceptJson)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            using var response = await _http.SendAsync(request);
            return new ZeroFactorProtocolResponse(
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                response.Headers.Location,
                await response.Content.ReadAsStringAsync()
            );
        }
        catch
        {
            throw new ZeroFactorProtocolFailure("The local provider request failed.");
        }
    }

    public void Dispose() => _http.Dispose();

    internal ZeroFactorAuthorizationRequest NewAuthorization(string callback)
    {
        var verifier = NewVerifier();
        var state = NewRandomToken(24);
        var nonce = NewRandomToken(24);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new ZeroFactorAuthorizationRequest(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = callback,
                ["response_type"] = "code",
                ["scope"] = "openid profile email",
                ["state"] = state,
                ["nonce"] = nonce,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = ChallengeMethod,
                ["prompt"] = "login",
            },
            verifier,
            state,
            nonce
        );
    }

    internal Uri BuildQuery(Uri endpoint, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join(
            "&",
            values.Select(pair =>
                Uri.EscapeDataString(pair.Key)
                + "="
                + Uri.EscapeDataString(pair.Value).Replace("%20", "+", StringComparison.Ordinal)
            )
        );
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    internal static string NewVerifier() => NewRandomToken(48);

    private static string NewRandomToken(int bytes) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    internal static Dictionary<string, string[]> ParseQuery(string query)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (
            var field in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        )
        {
            var pair = field.Split('=', 2);
            var name = DecodeQueryPart(pair[0]);
            if (!values.TryGetValue(name, out var existing))
            {
                existing = [];
                values.Add(name, existing);
            }

            existing.Add(pair.Length == 2 ? DecodeQueryPart(pair[1]) : string.Empty);
        }

        return values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal
        );
    }

    internal static string? Value(IReadOnlyDictionary<string, string[]> query, string name) =>
        query.TryGetValue(name, out var values) && values.Length == 1 ? values[0] : null;

    private static string DecodeQueryPart(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    internal static bool IsCallback(Uri location, string callback)
    {
        var expected = new Uri(callback);
        return location.Scheme == expected.Scheme
            && location.Host == expected.Host
            && location.Port == expected.Port
            && location.AbsolutePath == expected.AbsolutePath
            && string.IsNullOrEmpty(location.UserInfo)
            && string.IsNullOrEmpty(location.Fragment);
    }

    private static Uri ReadEndpoint(JsonElement root, string name)
    {
        var value = ReadString(root, name);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new ZeroFactorProtocolFailure("Discovery contained an invalid endpoint.");
    }

    internal static string ReadString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? string.Empty;

    internal static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            throw new ZeroFactorProtocolFailure("A provider response could not be parsed as JSON.");
        }
    }

    internal static JsonDocument ParseJson(byte[] bytes) =>
        ParseJson(Encoding.UTF8.GetString(bytes));

    private static (string? Action, string? Auth) ParseLoginForm(string html)
    {
        var form = Regex
            .Matches(html, "<form\\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .FirstOrDefault(match => Attribute(match.Value, "id") == "login-form");
        var input = Regex
            .Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .FirstOrDefault(match => Attribute(match.Value, "name") == "auth");
        return (
            form is null ? null : Attribute(form.Value, "action"),
            input is null ? null : Attribute(input.Value, "value")
        );
    }

    private static string? Attribute(string tag, string name)
    {
        var pattern =
            @"(?:^|\s)"
            + Regex.Escape(name)
            + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))";
        var match = Regex.Match(
            tag,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value) : null;
    }

    private static void Require(bool condition, string safeDiagnostic)
    {
        if (!condition)
        {
            throw new ZeroFactorProtocolFailure(safeDiagnostic);
        }
    }
}
