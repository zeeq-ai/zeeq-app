using Zeeq.Core.Documents;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Implements <see cref="INotionTokenValidator"/> by resolving a pooled, resilience-wrapped
/// <see cref="IZeeqNotionClient"/> for the pasted token and calling its connection identity
/// check.
/// </summary>
public sealed class NotionTokenValidator(IZeeqNotionClientFactory clients) : INotionTokenValidator
{
    /// <inheritdoc />
    public async Task<NotionTokenIdentity?> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        using var client = clients.Create(accessToken);
        var identity = await client.GetConnectionIdentityAsync(cancellationToken);

        return identity is null ? null : new NotionTokenIdentity(identity.BotUserId, identity.Name);
    }
}
