namespace Zeeq.Core.Identity;

/// <summary>
/// Runtime catalog of external login providers exposed by the auth UI.
/// </summary>
/// <remarks>
/// The catalog normalizes configured provider entries into the provider set the
/// application supports today. The local mock provider is always present for
/// development, while real providers are included only when their configuration is
/// complete enough to run a server-side authorization-code flow.
/// </remarks>
public sealed class ProviderAuthCatalog
{
    private readonly Dictionary<string, ProviderAuthSettings> _providers;

    private ProviderAuthCatalog(IEnumerable<ProviderAuthSettings> providers)
    {
        _providers = providers.ToDictionary(
            provider => provider.Name,
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Configured providers available to the login UI and login endpoints.
    /// </summary>
    public IReadOnlyCollection<ProviderAuthSettings> Providers => _providers.Values;

    /// <summary>
    /// Finds a provider by its stable local provider key.
    /// </summary>
    public ProviderAuthSettings? Find(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;

    /// <summary>
    /// Builds the provider catalog from app settings and provider-specific entries.
    /// </summary>
    /// <remarks>
    /// The mock provider remains available for local development. Real providers
    /// are included from configuration so appsettings can carry non-secret metadata
    /// while user secrets or environment variables provide client credentials.
    /// </remarks>
    public static ProviderAuthCatalog Create(AuthSettings settings, bool includeMock = true)
    {
        // The local mock provider is included by default so auth and DCR flows
        // can be exercised without configuring Google/O365 credentials.
        // In non-development environments, callers should pass includeMock: false.
        var providers = new List<ProviderAuthSettings>();

        if (includeMock)
        {
            providers.Add(
                new ProviderAuthSettings
                {
                    Name = "mock",
                    DisplayName = "Mock OAuth2",
                    ClientId = settings.MockClientId,
                    IssuerUri = settings.MockProviderIssuer,
                    ServerCallbackUri = settings.MockCallbackUri,
                    Scopes = ["openid", "profile", "email"],
                }
            );
        }

        // Configured providers may be disabled at runtime if required fields are missing.
        foreach (var provider in settings.Providers)
        {
            providers.Add(provider);
        }

        return new ProviderAuthCatalog(providers);
    }
}
