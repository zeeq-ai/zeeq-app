namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// GitHub-related configuration options.
    /// </summary>
    public GitHubSettings GitHub { get; init; } = new();
}

/// <summary>
/// Configuration options for connecting to GitHub as an app.
/// </summary>
public record GitHubSettings
{
    /// <summary>
    /// Always uses the <c>GH_TOKEN</c> environment variable for repository sync.
    /// </summary>
    /// <remarks>
    /// This is a local-development escape hatch for sync operations
    /// </remarks>
    public bool AlwaysUseGhTokenForSync { get; init; }

    /// <summary>
    /// The GitHub App ID for authentication.
    /// </summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>
    /// The GitHub Client ID for authentication.
    /// </summary>
    /// <remarks>
    /// Used to generate a JWT.
    ///
    /// See: https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app
    /// </remarks>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// The GitHub App's private key in PEM format for authentication.
    /// </summary>
    public string PrivateKeyPem { get; init; } = string.Empty;

    /// <summary>
    /// The URL slug of the GitHub App, used to construct the installation URL.
    /// </summary>
    /// <remarks>
    /// This is the lowercase, hyphenated name shown in the GitHub App's URL:
    /// <c>https://github.com/apps/{AppSlug}/installations/new</c>.
    /// Find it on the GitHub App settings page under "Public link".
    /// Set via <c>AppSettings__GitHub__AppSlug</c> in user secrets or environment variables.
    /// </remarks>
    public string AppSlug { get; init; } = string.Empty;

    /// <summary>
    /// The shared secret configured in the GitHub App's webhook settings.
    /// </summary>
    /// <remarks>
    /// Used to validate the <c>X-Hub-Signature-256</c> header on incoming webhook requests.
    /// Set via <c>AppSettings__GitHub__WebhookSecret</c> in user secrets or environment variables.
    /// </remarks>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Indicates whether the GitHub App has been configured with valid secrets.
    /// </summary>
    /// <returns>
    /// <c>true</c> if both the private key and webhook secret are configured; otherwise, <c>false</c>.
    /// </returns>
    public bool HasConfiguredSecrets =>
        !string.IsNullOrEmpty(PrivateKeyPem)
        && !string.IsNullOrEmpty(WebhookSecret)
        && !PrivateKeyPem.StartsWith("dotnet user-secrets set")
        && !WebhookSecret.StartsWith("dotnet user-secrets set");
}
