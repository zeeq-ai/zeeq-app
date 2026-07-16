namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// OpenAI related configuration settings.
    /// </summary>
    public OpenAISettings OpenAI { get; init; } = new();
}

/// <summary>
/// The OpenAI settings for initializing the OpenAI client.
/// </summary>
public record OpenAISettings
{
    /// <summary>
    /// The API key for authenticating with the OpenAI service.
    /// </summary>
    public string ApiKey
    {
        get => (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? field).Trim();
        init;
    } = string.Empty;
}
