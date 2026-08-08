namespace Zeeq.Core.Documents;

/// <summary>Provider-specific configuration for an externally-sourced library.</summary>
/// <remarks>
/// Shaped as a "one of" (nullable per-provider members) rather than a polymorphic type,
/// so SharePoint/Confluence/etc. are additive later without EF JSON-owned-entity
/// inheritance support. <see cref="Library.SourceKind"/> remains the discriminator.
/// </remarks>
public sealed record LibraryExternalSource
{
    /// <summary>Set when <c>SourceKind == "Notion"</c>; null otherwise.</summary>
    public NotionSourceConfiguration? Notion { get; init; }
}

/// <summary>Notion workspace connection + webhook subscription state for one library.</summary>
/// <remarks>
/// The connection itself is created in Notion (Access token method); Zeeq only holds
/// the pasted token and the webhook subscription state. 1:1 with the owning library.
/// </remarks>
public sealed record NotionSourceConfiguration
{
    /// <summary>
    /// Notion-side connection name — the display identity for this source.
    /// </summary>
    /// <remarks>
    /// This, NOT the workspace name, is what surfaces in the library source summary:
    /// in Notion, pages are associated with <i>connections</i>, so the connection is the
    /// meaningful unit for a user reconciling "which Notion thing feeds this library."
    /// Captured at library-create time from the bot identity behind the pasted token
    /// (<c>IZeeqNotionClient.GetConnectionIdentityAsync</c>).
    ///
    /// UNVERIFIED ASSUMPTION: this assumes the bot identity's name equals the connection
    /// name shown in Notion's UI. Not yet confirmed against a live token/workspace. If it
    /// does not hold, the create-library UI should let the user type the name instead of
    /// displaying a mismatched value.
    /// </remarks>
    public string? ConnectionName { get; init; }

    /// <summary>Notion workspace id, captured on first successful token validation. Display only.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Notion workspace name, for display in the library source summary.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary><see cref="Zeeq.Core.Models.EncryptedValue"/> id holding the pasted Notion access token.</summary>
    public required string AccessTokenValueId { get; init; }

    /// <summary>
    /// <see cref="Zeeq.Core.Models.EncryptedValue"/> id holding the webhook <c>verification_token</c>,
    /// which is also the HMAC signing key for <c>X-Notion-Signature</c>.
    /// </summary>
    /// <remarks>
    /// Null until Notion's one-time challenge POST lands. A challenge is accepted ONLY
    /// while this is null (first-write-wins); an authenticated "Reset webhook" action clears it.
    /// </remarks>
    public string? VerificationTokenValueId { get; init; }

    /// <summary>Notion's subscription id, recorded post-activation for diagnostics.</summary>
    public string? WebhookSubscriptionId { get; init; }

    /// <summary>When the webhook completed verification.</summary>
    public DateTimeOffset? WebhookActivatedAtUtc { get; init; }

    /// <summary>
    /// Monotonic serial embedded in the signed callback URL, enabling revocation.
    /// </summary>
    /// <remarks>
    /// AES-GCM's random nonce means every Protect() emits a different string for the
    /// same payload, so the serial is the only thing a validator can compare against
    /// to invalidate a previously-issued URL. Bumping it invalidates all outstanding URLs.
    /// </remarks>
    public int CallbackTokenSerial { get; init; }
}
