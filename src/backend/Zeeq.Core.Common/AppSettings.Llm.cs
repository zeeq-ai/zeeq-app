namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Shared LLM model, key, and encryption settings.
    /// </summary>
    public LlmSettings Llm { get; init; } = new();
}

/// <summary>
/// Settings for shared LLM clients and encrypted tenant key storage.
/// </summary>
/// <remarks>
/// The app-level default API key is configuration-owned and distinct from
/// tenant-owned encrypted keys. Local development can set only the Fast key in
/// user secrets; High and Max fall back to Fast when their API key value is blank.
/// </remarks>
public sealed record LlmSettings
{
    /// <summary>
    /// App-level default model and key settings by Zeeq quality tier.
    /// </summary>
    public LlmModelDefaults Models { get; init; } = new();

    /// <summary>
    /// Platform-level embedding settings used for snippet indexing and search.
    /// </summary>
    public LlmEmbeddingSettings Embeddings { get; init; } = new();

    /// <summary>
    /// Active encryption provider used for newly encrypted tenant key rows.
    /// </summary>
    public string EncryptionProvider
    {
        get => field.Trim();
        init;
    } = "data-protection";

    /// <summary>
    /// Local Data Protection key-ring path used by the `data-protection` provider.
    /// </summary>
    public string DataProtectionKeyRingPath
    {
        get => field.Trim();
        init;
    } = ".secrets/data-protection-keys";

    /// <summary>
    /// Google Cloud KMS crypto key resource name used by the `cloud-kms` provider.
    /// </summary>
    public string GoogleKmsKeyName
    {
        get => field.Trim();
        init;
    } = string.Empty;
}

/// <summary>
/// Platform-level (not per-org) embedding configuration for snippet indexing and search.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="LlmModelDefaults"/> — embeddings use a fixed
/// platform model so every vector in an org/library stays in one comparable vector space,
/// and this key is rotated independently of the chat-completion keys. Never per-org
/// configurable, so it does not go through the tenant-owned encrypted key store.
/// </remarks>
public sealed record LlmEmbeddingSettings
{
    /// <summary>
    /// Master switch. When false, snippet vectors are skipped entirely — the full-text
    /// search arm remains fully functional either way.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// OpenAI-compatible embeddings endpoint.
    /// </summary>
    public string Endpoint
    {
        get => field.Trim();
        init;
    } = "https://api.fireworks.ai/inference/v1";

    /// <summary>
    /// Embedding model identifier.
    /// </summary>
    public string Model
    {
        get => field.Trim();
        init;
    } = "accounts/fireworks/models/qwen3-embedding-8b";

    /// <summary>
    /// API key. Bound from <c>AppSettings:Llm:Embeddings:ApiKey</c> — Secret Manager in
    /// production, user-secrets locally. Required outside Development when
    /// <see cref="Enabled"/> is true.
    /// </summary>
    public string ApiKey
    {
        get => field.Trim();
        init;
    } = string.Empty;

    /// <summary>
    /// Embedding dimension. Matryoshka-truncated from the model's native dimension via
    /// Fireworks' <c>dimensions</c> request parameter; stored as <c>halfvec(768)</c>.
    /// </summary>
    public int Dimensions { get; init; } = 768;
}

/// <summary>
/// App-level default LLM configuration for the Fast, High, and Max tiers.
/// </summary>
public sealed record LlmModelDefaults
{
    private LlmModelDefault _high = new()
    {
        Provider = "Fireworks",
        Model = "accounts/fireworks/models/deepseek-v4-pro",
        Endpoint = "https://api.fireworks.ai/inference/v1",
    };

    private LlmModelDefault _max = new()
    {
        Provider = "Fireworks",
        Model = "accounts/fireworks/models/glm-5p2",
        Endpoint = "https://api.fireworks.ai/inference/v1",
    };

    /// <summary>
    /// Fast model tier, optimized for speed and local smoke testing.
    /// </summary>
    public LlmModelDefault Fast { get; init; } =
        new()
        {
            Provider = "Fireworks",
            Model = "accounts/fireworks/models/deepseek-v4-flash",
            Endpoint = "https://api.fireworks.ai/inference/v1",
        };

    /// <summary>
    /// Higher-quality tier. Its API key falls back to Fast when blank.
    /// </summary>
    public LlmModelDefault High
    {
        get => _high.WithApiKeyFallback(Fast.ApiKey);
        init => _high = value;
    }

    /// <summary>
    /// Maximum-quality tier. Its API key falls back to Fast when blank.
    /// </summary>
    public LlmModelDefault Max
    {
        get => _max.WithApiKeyFallback(Fast.ApiKey);
        init => _max = value;
    }
}

/// <summary>
/// App-level default model and API key configuration for one LLM tier.
/// </summary>
public sealed record LlmModelDefault
{
    /// <summary>
    /// Provider name, such as Fireworks, OpenAI, or Anthropic.
    /// </summary>
    public string Provider
    {
        get => field.Trim();
        init;
    } = "Fireworks";

    /// <summary>
    /// Provider model identifier.
    /// </summary>
    public string Model
    {
        get => field.Trim();
        init;
    } = string.Empty;

    /// <summary>
    /// API key used only for app-level default credentials.
    /// </summary>
    public string ApiKey
    {
        get => field.Trim();
        init;
    } = string.Empty;

    /// <summary>
    /// Optional OpenAI-compatible endpoint used by providers such as Fireworks.
    /// </summary>
    public string Endpoint
    {
        get => field.Trim();
        init;
    } = string.Empty;

    internal LlmModelDefault WithApiKeyFallback(string fallbackApiKey)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return this;
        }

        return this with
        {
            ApiKey = fallbackApiKey,
        };
    }
}
