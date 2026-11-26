using System.Xml.Serialization;

namespace Zeeq.Core.Models;

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
