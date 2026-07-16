namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Gets the static runtime configuration state.
    /// </summary>
    public static RuntimeConfig Runtime { get; } = new RuntimeConfig();
}

/// <summary>
/// Static runtime configuration state.
/// </summary>
public class RuntimeConfig
{
    /// <summary>
    /// In CI, always use the Postgres messaging implementation because the GCP
    /// Pub/Sub emulator requires loading the service account credentials which
    /// will fail in CI.
    /// </summary>
    public static bool ForcePostgresMessaging =>
        Environment.GetEnvironmentVariable("CI") == "true"
        || Environment.GetEnvironmentVariable("CI") == "1";

    /// <summary>
    /// Returns true if one of either the `DOTNET_ENVIRONMENT` or
    /// `ASPNETCORE_ENVIRONMENT` environment variables is set to "Development"
    /// (case-insensitive).
    /// </summary>
    public static bool IsDevelopment =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")?.ToLowerInvariant()
            == "development"
        || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.ToLowerInvariant()
            == "development";

    /// <summary>
    /// Returns true if the application is running in a Continuous Integration (CI) environment.
    /// </summary>
    public static bool IsCI =>
        Environment.GetEnvironmentVariable("CI") == "true"
        || Environment.GetEnvironmentVariable("CI") == "1";

    /// <summary>
    /// Returns true if the application is running in GitHub Codespaces.
    /// </summary>
    public static bool IsCodespaces =>
        Environment.GetEnvironmentVariable("CODESPACES") == "true"
        || !string.IsNullOrWhiteSpace(CodespaceName);

    /// <summary>
    /// Gets the current Codespaces name, when available.
    /// </summary>
    /// <value>The Codespaces name or an empty string.</value>
    public static string CodespaceName =>
        Environment.GetEnvironmentVariable("CODESPACE_NAME") ?? string.Empty;

    /// <summary>
    /// True when the application is running in a GitHub Copilot Agent runtime environment
    /// (a GitHub Action).  This is set via the `copilot-setup-steps.yml` workflow file in
    /// the `.github/workflows` directory.
    /// </summary>
    public static bool IsCopilotAgentRuntime =>
        Environment.GetEnvironmentVariable("COPILOT_AGENT_RUNTIME") == "true";
}
