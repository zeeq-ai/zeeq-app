using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Validates and serializes the canonical code review XML output contract.
/// </summary>
public sealed class CodeReviewXmlOutputValidator
{
    private static readonly XmlSerializer Serializer = new(typeof(CodeReviewOutputDocument));
    private static readonly XmlSerializerNamespaces EmptyNamespaces = new([XmlQualifiedName.Empty]);

    /// <summary>
    /// Parses an aggregate <c>&lt;reviews&gt;</c> XML document into the output model.
    /// </summary>
    public CodeReviewXmlValidationResult Validate(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return CodeReviewXmlValidationResult.Invalid("Review XML is empty.");
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            var document = XDocument.Load(reader);

            // Normalize recoverable deviations before shape validation so old artifacts
            // and common model mistakes are healed rather than rejected.
            NormalizeFindingShapes(document);
            NormalizeFindingLevels(document);

            var rawShapeError = ValidateRawXmlShape(document.Root);

            if (rawShapeError is not null)
            {
                return CodeReviewXmlValidationResult.Invalid(rawShapeError);
            }

            var output = (CodeReviewOutputDocument?)Serializer.Deserialize(document.CreateReader());

            if (output is null)
            {
                return CodeReviewXmlValidationResult.Invalid("Review XML did not deserialize.");
            }

            if (!output.NoAgentsActivated && output.Reviews.Count == 0)
            {
                return CodeReviewXmlValidationResult.Invalid(
                    "Review XML must contain at least one review when noAgentsActivated is false."
                );
            }

            var contractError = ValidateContract(output, document.Root);

            if (contractError is not null)
            {
                return CodeReviewXmlValidationResult.Invalid(contractError);
            }

            return CodeReviewXmlValidationResult.Valid(output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or XmlException)
        {
            return CodeReviewXmlValidationResult.Invalid(ex.Message);
        }
    }

    /// <summary>
    /// Wraps one reviewer block in a root document and validates it.
    /// </summary>
    /// <remarks>
    /// Extracts the canonical <c>&lt;review&gt;…&lt;/review&gt;</c> span before wrapping so
    /// that model preamble, postscript, or markdown code-fence wrappers are stripped
    /// without requiring the model to produce a bare XML block. If neither boundary tag
    /// is found the block is structurally invalid regardless.
    /// </remarks>
    public CodeReviewXmlValidationResult ValidateReviewerBlock(string reviewerXml)
    {
        var extracted = ExtractReviewBlock(reviewerXml);

        if (extracted is null)
        {
            return CodeReviewXmlValidationResult.Invalid(
                "Reviewer output did not contain a <review> block."
            );
        }

        var validation = Validate($"""<reviews noAgentsActivated="false">{extracted}</reviews>""");

        if (!validation.IsValid || validation.Output is null)
        {
            return validation;
        }

        return validation.Output.Reviews.Count == 1
            ? validation
            : CodeReviewXmlValidationResult.Invalid(
                $"Reviewer XML must contain exactly one review block; found {validation.Output.Reviews.Count}."
            );
    }

    /// <summary>
    /// Slices the first <c>&lt;review</c> opening tag through the last <c>&lt;/review&gt;</c>
    /// closing tag, discarding any surrounding prose or code-fence wrappers.
    /// Returns <c>null</c> when either boundary is absent.
    /// </summary>
    private static string? ExtractReviewBlock(string text)
    {
        var start = text.IndexOf("<review", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var endTag = "</review>";
        var end = text.LastIndexOf(endTag, StringComparison.Ordinal);
        if (end < start)
        {
            return null;
        }

        return text[start..(end + endTag.Length)];
    }

    /// <summary>
    /// Creates a schema-valid failed reviewer placeholder for retry exhaustion.
    /// </summary>
    public CodeReviewFacetOutput CreateFailedReviewerPlaceholder(
        CodeReviewerRuntimeAgent agent,
        string failureMessage
    ) =>
        new()
        {
            Facet = agent.ReviewFacet,
            Agent = agent.DisplayName,
            Summary = "Reviewer output could not be validated.",
            Details = "This reviewer failed output validation after retrying its response.",
            Findings =
            [
                new()
                {
                    Level = CodeReviewFindingLevel.Major,
                    File = "(reviewer-output)",
                    Summary = "Reviewer output failed validation",
                    Details = failureMessage,
                },
            ],
        };

    /// <summary>
    /// Serializes a single reviewer facet block without the outer <c>&lt;reviews&gt;</c> wrapper.
    /// </summary>
    /// <remarks>
    /// Reviewer workflow nodes exchange facet blocks so the aggregation node can
    /// preserve the V1 fan-in shape. The canonical storage artifact is still the
    /// outer <c>&lt;reviews&gt;</c> document produced after aggregation.
    /// </remarks>
    public string SerializeReviewerBlock(CodeReviewFacetOutput output)
    {
        var document = new CodeReviewOutputDocument { Reviews = [output] };
        var xml = Serialize(document);
        var reviewElement = XDocument.Parse(xml).Root?.Element("review");

        if (reviewElement is null)
        {
            throw new InvalidOperationException(
                "Serialized reviewer block did not contain a review element."
            );
        }

        return reviewElement.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Serializes an output document without an XML declaration or namespaces.
    /// </summary>
    public static string Serialize(CodeReviewOutputDocument output)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true };
        using var writer = new StringWriter();
        using var xmlWriter = XmlWriter.Create(writer, settings);

        Serializer.Serialize(xmlWriter, output, EmptyNamespaces);

        return ConvertProseToCData(writer.ToString());
    }

    private static string? ValidateContract(CodeReviewOutputDocument output, XElement? root)
    {
        if (output.NoAgentsActivated)
        {
            return output.Reviews.Count == 0
                ? null
                : "Review XML must not contain review blocks when noAgentsActivated is true.";
        }

        var reviewElements = root?.Elements("review").ToArray() ?? [];

        if (reviewElements.Length != output.Reviews.Count)
        {
            return "Review XML review element count did not match the deserialized review count.";
        }

        for (var reviewIndex = 0; reviewIndex < output.Reviews.Count; reviewIndex++)
        {
            var review = output.Reviews[reviewIndex];
            var reviewPath = $"review[{reviewIndex}]";
            var reviewError = ValidateReview(review, reviewPath);

            if (reviewError is not null)
            {
                return reviewError;
            }

            var findingElements =
                reviewElements[reviewIndex].Element("findings")?.Elements("finding").ToArray()
                ?? [];

            if (findingElements.Length != review.Findings.Count)
            {
                return $"{reviewPath} finding element count did not match the deserialized finding count.";
            }

            for (var findingIndex = 0; findingIndex < review.Findings.Count; findingIndex++)
            {
                var findingError = ValidateFinding(
                    review.Findings[findingIndex],
                    findingElements[findingIndex],
                    $"{reviewPath}.finding[{findingIndex}]"
                );

                if (findingError is not null)
                {
                    return findingError;
                }
            }
        }

        return null;
    }

    private static string? ValidateRawXmlShape(XElement? root)
    {
        if (root is null)
        {
            return "Review XML did not contain a root element.";
        }

        return null;
    }

    /// <summary>
    /// Normalizes recoverable deviations in review and finding shape before deserialization.
    /// </summary>
    /// <remarks>
    /// Handles four common model mistakes / legacy formats:
    /// <list type="bullet">
    ///   <item><description><c>&lt;review_summary&gt;</c> → <c>&lt;summary&gt;</c> rename.</description></item>
    ///   <item><description>Legacy <c>summary="…"</c> attribute on <c>&lt;finding&gt;</c> → <c>&lt;summary&gt;</c> child element.</description></item>
    ///   <item><description>Legacy CDATA/text body directly inside <c>&lt;finding&gt;</c> → <c>&lt;details&gt;</c> child element.</description></item>
    ///   <item><description>Legacy <c>&lt;body&gt;</c> child element → <c>&lt;details&gt;</c> rename (pre-rename stored artifacts).</description></item>
    /// </list>
    /// </remarks>
    private static void NormalizeFindingShapes(XDocument document)
    {
        foreach (var reviewElement in document.Descendants("review"))
        {
            var reviewSummaryEl = reviewElement.Element("review_summary");

            reviewSummaryEl?.Name = "summary";
        }

        foreach (var findingElement in document.Descendants("finding"))
        {
            var summaryAttr = findingElement.Attribute("summary");

            if (summaryAttr is not null && findingElement.Element("summary") is null)
            {
                findingElement.AddFirst(new XElement("summary", summaryAttr.Value));
                summaryAttr.Remove();
            }

            // Rename legacy <body> → <details> so all finding content is canonical.
            var bodyEl = findingElement.Element("body");

            if (bodyEl is not null && findingElement.Element("details") is null)
            {
                bodyEl.Name = "details";
            }

            if (findingElement.Element("details") is null)
            {
                var directText = string.Concat(
                    findingElement.Nodes().OfType<XText>().Select(t => t.Value)
                );

                var trimmed = directText.Trim();

                if (!string.IsNullOrEmpty(trimmed))
                {
                    findingElement.Nodes().OfType<XText>().ToList().ForEach(t => t.Remove());
                    findingElement.Add(new XElement("details", trimmed));
                }
            }
        }
    }

    private static string? ValidateReview(CodeReviewFacetOutput review, string reviewPath)
    {
        if (string.IsNullOrWhiteSpace(review.Facet))
        {
            return $"{reviewPath} must include a non-empty facet attribute.";
        }

        if (string.IsNullOrWhiteSpace(review.Agent))
        {
            return $"{reviewPath} must include a non-empty agent attribute.";
        }

        if (string.IsNullOrWhiteSpace(review.Summary))
        {
            return $"{reviewPath} must include a non-empty summary.";
        }

        if (string.IsNullOrWhiteSpace(review.Details))
        {
            return $"{reviewPath} must include non-empty details.";
        }

        return null;
    }

    private static string? ValidateFinding(
        CodeReviewFindingOutput finding,
        XElement findingElement,
        string findingPath
    )
    {
        var rawLevel = findingElement.Attribute("level")?.Value;

        if (string.IsNullOrWhiteSpace(rawLevel))
        {
            return $"{findingPath} must include a non-empty level attribute.";
        }

        if (!TryParseFindingLevel(rawLevel, out _))
        {
            return $"{findingPath} has unsupported level '{rawLevel}'.";
        }

        if (string.IsNullOrWhiteSpace(finding.File))
        {
            return $"{findingPath} must include a non-empty file attribute.";
        }

        if (string.IsNullOrWhiteSpace(finding.Summary))
        {
            return $"{findingPath} must include a non-empty summary.";
        }

        if (string.IsNullOrWhiteSpace(finding.Details))
        {
            return $"{findingPath} must include non-empty details.";
        }

        return null;
    }

    private static void NormalizeFindingLevels(XDocument document)
    {
        foreach (var findingElement in document.Descendants("finding"))
        {
            var level = findingElement.Attribute("level");
            if (level is not null && TryParseFindingLevel(level.Value, out var parsedLevel))
            {
                level.Value = ToXmlLevel(parsedLevel);
            }
        }
    }

    private static bool TryParseFindingLevel(
        string value,
        out CodeReviewFindingLevel parsedLevel
    ) => Enum.TryParse(value, ignoreCase: true, out parsedLevel);

    private static string ToXmlLevel(CodeReviewFindingLevel level) =>
        level switch
        {
            CodeReviewFindingLevel.Critical => "CRITICAL",
            CodeReviewFindingLevel.Major => "MAJOR",
            CodeReviewFindingLevel.Minor => "MINOR",
            CodeReviewFindingLevel.Suggestion => "SUGGESTION",
            CodeReviewFindingLevel.Comment => "COMMENT",
            _ => level.ToString(),
        };

    /// <summary>
    /// Post-processes serialized XML to wrap all model-authored prose elements in CDATA sections.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlSerializer" /> emits entity-escaped text for string properties.
    /// Replacing those text nodes with CDATA sections makes the stored XML safe for
    /// arbitrary Markdown content (code fences, generics, HTML-like snippets) without
    /// requiring the model to manually escape anything.
    /// <para>
    /// NOTE: This is a second parse/rewrite pass over already-serialized XML. For this call
    /// frequency (once per completed review job) the overhead is negligible. If this path
    /// ever becomes hot, replace with a <c>CDataXmlWriter : XmlWriter</c> wrapper that
    /// intercepts <c>WriteString</c> during the primary <see cref="Serialize"/> pass and
    /// emits <c>WriteCData</c> for the target element names, eliminating the extra
    /// <c>XDocument.Parse</c> + <c>ToString</c> allocation entirely. The implementation
    /// requires delegating all ~20 abstract <c>XmlWriter</c> members to an inner writer.
    /// </para>
    /// </remarks>
    private static string ConvertProseToCData(string serializedXml)
    {
        var document = XDocument.Parse(serializedXml);

        foreach (var reviewElement in document.Descendants("review"))
        {
            WrapInCData(reviewElement.Element("summary"));
            WrapInCData(reviewElement.Element("details"));
        }

        foreach (var findingElement in document.Descendants("finding"))
        {
            WrapInCData(findingElement.Element("summary"));
            WrapInCData(findingElement.Element("details"));
        }

        return document.ToString(SaveOptions.None);
    }

    private static void WrapInCData(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        var text = element.Value;
        element.RemoveNodes();
        element.Add(new XCData(text));
    }
}

/// <summary>
/// Result returned by <see cref="CodeReviewXmlOutputValidator" /> validation.
/// </summary>
public sealed record CodeReviewXmlValidationResult(
    bool IsValid,
    CodeReviewOutputDocument? Output,
    string? ErrorMessage
)
{
    /// <summary>
    /// Creates a valid validation result.
    /// </summary>
    public static CodeReviewXmlValidationResult Valid(CodeReviewOutputDocument output) =>
        new(true, output, null);

    /// <summary>
    /// Creates an invalid validation result.
    /// </summary>
    public static CodeReviewXmlValidationResult Invalid(string errorMessage) =>
        new(false, null, errorMessage);
}
