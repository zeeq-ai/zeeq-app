namespace Zeeq.Core.Documents;

/// <summary>
/// Validates a pasted Notion access token and returns the bot/connection identity behind it.
/// </summary>
/// <remarks>
/// This is the application-facing port for Notion token validation (zeeq-ai/zeeq-app#193).
/// <c>Zeeq.Platform.Documents</c> depends on this instead of the concrete Notion SDK client
/// factory in <c>Zeeq.Integrations.Notion</c> — the same anti-corruption discipline
/// <c>Zeeq.Integrations.Notion.IZeeqNotionClient</c> already applies to keep Notion SDK types
/// out of the ingest pipeline now also keeps the concrete Notion integration assembly out of
/// the create-library API handler's project reference.
/// </remarks>
public interface INotionTokenValidator
{
    /// <summary>
    /// Validates the given access token against Notion and returns the bot/connection identity
    /// behind it.
    /// </summary>
    /// <returns><c>null</c> if the token is invalid or unauthorized.</returns>
    Task<NotionTokenIdentity?> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken
    );
}

/// <summary>The bot/connection identity behind a validated Notion access token.</summary>
public sealed record NotionTokenIdentity(string BotUserId, string Name);
