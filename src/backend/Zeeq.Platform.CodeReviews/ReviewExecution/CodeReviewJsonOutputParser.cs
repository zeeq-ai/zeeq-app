using System.Text.Json;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Parses a reviewer agent's JSON output into the canonical <see cref="CodeReviewFacetOutput"/> model.
/// </summary>
/// <remarks>
/// The reviewer LLM emits JSON per <see cref="CodeReviewSystemPrompt"/>. This parser is
/// intentionally tolerant of surrounding prose or markdown fences (it slices the first
/// <c>{</c> through the last <c>}</c>), then validates the same field-level invariants the
/// XML validator enforces before mapping onto the XML output model.
///
/// NOTE (deferred, see spec §4.4): this tolerant parse is the contract today. If a
/// provider-enforced json_schema response format is added later, this stays as the fallback
/// path for providers that do not enforce (or that emit prose around) the JSON.
/// </remarks>
internal static class CodeReviewJsonOutputParser
{
    // NOTE: The lenient options are intentional resilience for model-authored JSON, not a
    // hot-path concern. PropertyNameCaseInsensitive tolerates casing drift (e.g. "Summary" /
    // "SUMMARY" instead of "summary"), and comment / trailing-comma handling absorb common model
    // quirks — all of which would otherwise fail an otherwise-usable review. This parse runs once
    // per reviewer and is dominated by LLM latency, so the tolerance costs nothing meaningful.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Attempts to parse reviewer output into a validated <see cref="CodeReviewFacetOutput"/>.
    /// </summary>
    /// <param name="text">Raw reviewer model output, possibly with surrounding prose or fences.</param>
    /// <param name="agent">Runtime agent supplying the server-assigned facet/agent identity.</param>
    /// <param name="facet">The mapped output on success; otherwise <c>null</c>.</param>
    /// <param name="error">A human-readable error suitable for the correction prompt on failure.</param>
    public static bool TryParse(
        string text,
        CodeReviewerRuntimeAgent agent,
        out CodeReviewFacetOutput? facet,
        out string? error
    )
    {
        facet = null;

        var json = ExtractJsonObject(text);

        if (json is null)
        {
            error = "Reviewer output did not contain a JSON object.";
            return false;
        }

        CodeReviewFacetJson? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<CodeReviewFacetJson>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            error = $"Reviewer JSON did not parse: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "Reviewer JSON deserialized to null.";
            return false;
        }

        var validationError = Validate(parsed);

        if (validationError is not null)
        {
            error = validationError;
            return false;
        }

        facet = parsed.ToFacetOutput(agent);
        error = null;
        return true;
    }

    /// <summary>
    /// Slices the first <c>{</c> through the last <c>}</c>, discarding any surrounding prose
    /// or code-fence wrappers. Returns <c>null</c> when no brace pair is present.
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');

        if (start < 0 || end < start)
        {
            return null;
        }

        return text[start..(end + 1)];
    }

    private static string? Validate(CodeReviewFacetJson facet)
    {
        if (string.IsNullOrWhiteSpace(facet.Summary))
        {
            return "Reviewer JSON must include a non-empty summary.";
        }

        if (string.IsNullOrWhiteSpace(facet.Details))
        {
            return "Reviewer JSON must include non-empty details.";
        }

        for (var index = 0; index < facet.Findings.Count; index++)
        {
            var finding = facet.Findings[index];
            var path = $"findings[{index}]";

            if (
                string.IsNullOrWhiteSpace(finding.RawLevel)
                || !Enum.TryParse<CodeReviewFindingLevel>(finding.RawLevel, ignoreCase: true, out _)
            )
            {
                return $"{path} has a missing or unsupported level '{finding.RawLevel}'.";
            }

            if (string.IsNullOrWhiteSpace(finding.File))
            {
                return $"{path} must include a non-empty file.";
            }

            if (string.IsNullOrWhiteSpace(finding.Summary))
            {
                return $"{path} must include a non-empty summary.";
            }

            if (string.IsNullOrWhiteSpace(finding.Details))
            {
                return $"{path} must include non-empty details.";
            }
        }

        return null;
    }
}
