using System.Text.Json.Serialization;

namespace Zeeq.Platform.CodeReviews;

// This file is the JSON twin of CodeReviewOutputModels.cs. The reviewer LLM now emits
// JSON (see CodeReviewOutputPrompt); the shared ICodeReviewFacet / ICodeReviewFinding
// interfaces keep the JSON DTO and the XML output model provably field-aligned, and
// ToFacetOutput maps the parsed JSON onto the canonical XML model for outbound rendering.
//
// NOTE (deferred, see spec §4.4): CodeReviewFacetJson is schema-ready. A future pass can
// export a json_schema from this type via AIJsonUtilities and attach it as a
// ChatResponseFormatJson response format for provider-enforced structured output. That is
// intentionally NOT wired today because forcing a response format conflicts with mid-turn
// tool use (Anthropic structured output is beta). Example-driven prompting + tolerant parse
// is the contract for now.

// NOTE: These interfaces are intentional even though there are only two implementers and no
// polymorphic consumer beyond ToFacetOutput. Their purpose is to make JSON↔XML field parity
// explicit and compiler-checked (per design decision D1), not extensibility. A reviewer flagged
// them as a "transitory abstraction"; the parity/documentation value is the point, so they stay.

/// <summary>
/// Read-only projection of one reviewer facet, implemented by both the XML output model
/// (<see cref="CodeReviewFacetOutput"/>) and the inbound JSON DTO (<see cref="CodeReviewFacetJson"/>)
/// so their field parity is explicit and the mapper can be interface-driven.
/// </summary>
internal interface ICodeReviewFacet
{
    /// <summary>Short reviewer summary.</summary>
    string Summary { get; }

    /// <summary>Expanded reviewer details.</summary>
    string Details { get; }

    /// <summary>Findings for this facet.</summary>
    IReadOnlyList<ICodeReviewFinding> Findings { get; }
}

/// <summary>
/// Read-only projection of one finding, shared by the XML and JSON shapes.
/// </summary>
internal interface ICodeReviewFinding
{
    /// <summary>Finding severity.</summary>
    CodeReviewFindingLevel Level { get; }

    /// <summary>Repository-relative file path.</summary>
    string File { get; }

    /// <summary>Optional line number for inline findings; <c>null</c> when not line-scoped.</summary>
    int? Line { get; }

    /// <summary>Optional GitHub diff side, for example <c>RIGHT</c> or <c>LEFT</c>.</summary>
    string? Side { get; }

    /// <summary>Short finding summary.</summary>
    string Summary { get; }

    /// <summary>Full finding details.</summary>
    string Details { get; }
}

/// <summary>
/// Inbound JSON shape a reviewer agent emits for one facet. Deserialized tolerantly by
/// <see cref="CodeReviewJsonOutputParser"/> and mapped to <see cref="CodeReviewFacetOutput"/>.
/// </summary>
/// <remarks>
/// <c>facet</c> and <c>agent</c> are intentionally absent: they are not model-authored and
/// are stamped from <see cref="CodeReviewerRuntimeAgent"/> during mapping.
/// </remarks>
internal sealed class CodeReviewFacetJson : ICodeReviewFacet
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public string Details { get; init; } = string.Empty;

    [JsonPropertyName("findings")]
    public List<CodeReviewFindingJson> Findings { get; init; } = [];

    IReadOnlyList<ICodeReviewFinding> ICodeReviewFacet.Findings => Findings;
}

/// <summary>
/// Inbound JSON shape for one finding. <see cref="RawLevel"/> is the model-authored string;
/// the parsed <see cref="CodeReviewFindingLevel"/> is exposed via the interface after validation.
/// </summary>
internal sealed class CodeReviewFindingJson : ICodeReviewFinding
{
    [JsonPropertyName("level")]
    public string RawLevel { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public string Details { get; init; } = string.Empty;

    // Explicit so it is excluded from JSON (de)serialization; safe to read only after
    // CodeReviewJsonOutputParser has confirmed RawLevel parses.
    CodeReviewFindingLevel ICodeReviewFinding.Level =>
        Enum.TryParse<CodeReviewFindingLevel>(RawLevel, ignoreCase: true, out var parsed)
            ? parsed
            : CodeReviewFindingLevel.Comment;
}

/// <summary>
/// Maps the shared facet projection onto the canonical XML output model.
/// </summary>
internal static class CodeReviewFacetMapping
{
    /// <summary>
    /// Projects a validated <see cref="ICodeReviewFacet"/> onto a <see cref="CodeReviewFacetOutput"/>,
    /// stamping <paramref name="agent"/>-owned <c>facet</c>/<c>agent</c> identity.
    /// </summary>
    public static CodeReviewFacetOutput ToFacetOutput(
        this ICodeReviewFacet facet,
        CodeReviewerRuntimeAgent agent
    ) =>
        new()
        {
            Facet = agent.ReviewFacet,
            Agent = agent.DisplayName,
            Summary = facet.Summary,
            Details = facet.Details,
            Findings =
            [
                .. facet.Findings.Select(finding => new CodeReviewFindingOutput
                {
                    Level = finding.Level,
                    File = finding.File,
                    // CodeReviewFindingOutput.Line is a non-nullable int gated by ShouldSerializeLine();
                    // 0 means "no line" and is omitted from serialized XML.
                    Line = finding.Line ?? 0,
                    Side = finding.Side,
                    Summary = finding.Summary,
                    Details = finding.Details,
                }),
            ],
        };
}
