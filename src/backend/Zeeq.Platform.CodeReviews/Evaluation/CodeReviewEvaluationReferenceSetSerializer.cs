using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Canonical JSON serialization for dataset reference sets.
/// </summary>
/// <remarks>
/// Reference sets are stored as one jsonb document per case. Save always replaces the whole
/// collection inside the dataset version check, so a stable canonical form is enough; no
/// partial JSON path updates are needed and no GIN index exists.
/// </remarks>
public static class CodeReviewEvaluationReferenceSetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes reference sets for jsonb storage.</summary>
    public static string Serialize(IReadOnlyList<CodeReviewEvaluationReferenceSet> referenceSets) =>
        JsonSerializer.Serialize(referenceSets, Options);

    /// <summary>Deserializes stored reference sets.</summary>
    public static CodeReviewEvaluationReferenceSet[] Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<CodeReviewEvaluationReferenceSet[]>(json, Options) ?? [];
}
