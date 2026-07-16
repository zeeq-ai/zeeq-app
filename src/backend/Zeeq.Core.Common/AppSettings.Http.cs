namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// HTTP related configuration settings.
    /// </summary>
    public HttpSettings Http { get; init; } = new();
}

/// <summary>
/// HTTP settings for the application.
/// </summary>
public sealed record HttpSettings
{
    /// <summary>
    /// The public base URI of the API server (used as the OpenIddict token issuer).
    /// Must match the URL browsers use to reach the API so Bearer tokens validate correctly.
    /// </summary>
    public string ApiBaseUri { get; init; } = "http://zeeq-api.localhost:8095";

    /// <summary>
    /// The base URI of the SPA frontend.
    /// </summary>
    public string FrontendBaseUri { get; init; } = "http://zeeq-web.localhost:8095";

    /// <summary>
    /// The allowed CORS origins for the API server.
    /// </summary>
    /// <remarks>
    /// The front-end is always allowed; these are additional CORs origins.
    /// </remarks>
    public string[] AllowedCorsOrigins { get; init; } = [];
}
