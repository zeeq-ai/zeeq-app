namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Document and library workflow settings.
    /// </summary>
    public DocumentSettings Documents { get; init; } = new();
}

/// <summary>
/// Settings that control document and library workflows.
/// </summary>
public sealed record DocumentSettings
{
    /// <summary>
    /// Plain text key material used to derive the HMAC key for signed library export packages.
    /// </summary>
    /// <remarks>
    /// Store the real value in user secrets or environment variables. The value is hashed with a
    /// package-specific purpose before use; it is not expected to already be raw HMAC key bytes.
    /// </remarks>
    public string LibraryExportSigningKey { get; init; } = string.Empty;
}
