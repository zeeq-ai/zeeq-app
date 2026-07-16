using System.Xml.Serialization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Canonical XML root for one aggregated code review result.
/// </summary>
[XmlRoot("reviews")]
public sealed class CodeReviewOutputDocument
{
    /// <summary>
    /// True when repository configuration existed but no reviewer activated for the changed files.
    /// </summary>
    [XmlAttribute("noAgentsActivated")]
    public bool NoAgentsActivated { get; set; }

    /// <summary>
    /// Reviewer facet outputs contained in this aggregate.
    /// </summary>
    [XmlElement("review")]
    public List<CodeReviewFacetOutput> Reviews { get; set; } = [];
}

/// <summary>
/// XML output for one reviewer facet.
/// </summary>
/// <remarks>
/// Also implements <see cref="ICodeReviewFacet"/> so it is provably field-aligned with the
/// inbound JSON DTO (<see cref="CodeReviewFacetJson"/>). The interface members are explicit
/// where types differ, so they never affect XML serialization.
/// </remarks>
public sealed class CodeReviewFacetOutput : ICodeReviewFacet
{
    IReadOnlyList<ICodeReviewFinding> ICodeReviewFacet.Findings => Findings;

    /// <summary>
    /// Facet label, for example <c>Security</c> or <c>Performance</c>.
    /// </summary>
    [XmlAttribute("facet")]
    public string Facet { get; set; } = string.Empty;

    /// <summary>
    /// Reviewer display name.
    /// </summary>
    [XmlAttribute("agent")]
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    /// Short reviewer summary.
    /// </summary>
    [XmlElement("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Expanded reviewer details.
    /// </summary>
    [XmlElement("details")]
    public string Details { get; set; } = string.Empty;

    /// <summary>
    /// Findings for this facet.
    /// </summary>
    [XmlArray("findings")]
    [XmlArrayItem("finding")]
    public List<CodeReviewFindingOutput> Findings { get; set; } = [];
}

/// <summary>
/// XML output for one actionable review finding.
/// </summary>
/// <remarks>
/// Also implements <see cref="ICodeReviewFinding"/> for JSON parity. The only type mismatch is
/// <see cref="Line"/> (non-nullable int here, nullable on the interface), implemented explicitly
/// so it does not affect XML serialization.
/// </remarks>
public sealed class CodeReviewFindingOutput : ICodeReviewFinding
{
    int? ICodeReviewFinding.Line => ShouldSerializeLine() ? Line : null;

    /// <summary>
    /// Finding severity.
    /// </summary>
    [XmlAttribute("level")]
    public CodeReviewFindingLevel Level { get; set; }

    /// <summary>
    /// Repository-relative file path.
    /// </summary>
    [XmlAttribute("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Optional line number for inline findings.
    /// </summary>
    [XmlAttribute("line")]
    public int Line { get; set; }

    /// <summary>
    /// Whether <see cref="Line" /> should be serialized.
    /// </summary>
    public bool ShouldSerializeLine() => Line > 0;

    /// <summary>
    /// Optional GitHub diff side, for example <c>RIGHT</c> or <c>LEFT</c>.
    /// </summary>
    [XmlAttribute("side")]
    public string? Side { get; set; }

    /// <summary>
    /// Whether <see cref="Side" /> should be serialized.
    /// </summary>
    public bool ShouldSerializeSide() => !string.IsNullOrWhiteSpace(Side);

    /// <summary>
    /// Short finding summary. Serialized as a child element with CDATA so
    /// Markdown and XML-like content cannot break the attribute-level parser.
    /// </summary>
    [XmlElement("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Full finding details. Serialized as a child element with CDATA so Markdown,
    /// code fences, and XML-like snippets are safe.
    /// </summary>
    [XmlElement("details")]
    public string Details { get; set; } = string.Empty;

    /// <summary>
    /// Legacy alias for <see cref="Details" />. Preserved for backward compatibility
    /// with stored artifacts written before the <c>&lt;body&gt;</c> → <c>&lt;details&gt;</c>
    /// rename. Reads and writes through <see cref="Details" />.
    /// </summary>
    [Obsolete(
        "Use Details. Body is a legacy alias for artifacts written before the <body> → <details> rename.",
        DiagnosticId = "ZEEQ001"
    )]
    [XmlElement("body")]
    public string Body
    {
        get => Details;
        set => Details = value;
    }

    /// <summary>
    /// Suppresses <c>&lt;body&gt;</c> from new serialized output; <see cref="Details" /> is canonical.
    /// </summary>
    public bool ShouldSerializeBody() => false;
}

/// <summary>
/// Severity levels supported by the code review XML contract.
/// </summary>
public enum CodeReviewFindingLevel
{
    /// <summary>
    /// A blocking correctness, security, or data-loss issue.
    /// </summary>
    [XmlEnum("CRITICAL")]
    Critical,

    /// <summary>
    /// A serious issue that should be fixed before merge.
    /// </summary>
    [XmlEnum("MAJOR")]
    Major,

    /// <summary>
    /// A small correctness, maintainability, or test issue.
    /// </summary>
    [XmlEnum("MINOR")]
    Minor,

    /// <summary>
    /// A non-blocking improvement suggestion.
    /// </summary>
    [XmlEnum("SUGGESTION")]
    Suggestion,

    /// <summary>
    /// Informational feedback or architectural commentary.
    /// </summary>
    [XmlEnum("COMMENT")]
    Comment,
}
