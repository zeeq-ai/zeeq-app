using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Canonical content form for reviewer agent revisions.
/// </summary>
/// <remarks>
/// Deduplication compares configuration content, not row identity or labels. Timestamps and
/// operator labels are excluded. <c>Enabled</c> IS included: the stored revision must be a
/// faithful snapshot of the content that produced its hash, so two rows can never share a hash
/// while differing in stored status. A no-op save of identical content still deduplicates;
/// enabling or disabling records one honest revision rather than reusing a stale snapshot.
/// </remarks>
public static class CodeReviewerAgentRevisionContent
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes revision configuration content canonically.</summary>
    public static string Serialize(CodeReviewerAgentRevisionConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, Options);

    /// <summary>Deserializes revision configuration content.</summary>
    public static CodeReviewerAgentRevisionConfiguration Deserialize(string json) =>
        JsonSerializer.Deserialize<CodeReviewerAgentRevisionConfiguration>(json, Options)
        ?? throw new InvalidOperationException(
            "The reviewer agent revision content could not be read."
        );

    /// <summary>Computes the lower-case hexadecimal SHA-256 of the canonical content.</summary>
    /// <remarks>
    /// The hash covers every behavior-bearing field, including <c>Enabled</c>. A revision row
    /// must be a faithful snapshot of the content that produced its hash, so two rows can never
    /// share a hash while storing different configuration. A no-op save still deduplicates;
    /// enabling or disabling records an honest snapshot rather than silently reusing a row whose
    /// stored status differs from the live agent.
    /// </remarks>
    public static string ComputeHash(CodeReviewerAgentRevisionConfiguration configuration)
    {
        var content = new HashProjection(
            configuration.DisplayName,
            configuration.ReviewFacet,
            configuration.ModelTier,
            configuration.Prompt,
            configuration.Enabled,
            configuration.ActivationConfiguration
        );

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, Options)))
        );
    }

    /// <summary>Canonical projection used only for content hashing.</summary>
    private sealed record HashProjection(
        string DisplayName,
        string ReviewFacet,
        CodeReviewModelTier ModelTier,
        string Prompt,
        bool Enabled,
        CodeReviewerActivationConfiguration ActivationConfiguration
    );

    /// <summary>Captures the current content of a persisted reviewer agent.</summary>
    public static CodeReviewerAgentRevisionConfiguration Capture(CodeReviewerAgent agent) =>
        new(
            agent.DisplayName,
            agent.ReviewFacet,
            agent.ModelTier,
            agent.Prompt,
            agent.Enabled,
            agent.ActivationConfiguration
        );
}
