using System.Text.Json.Serialization;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents.Editing;

/// <summary>Outcome of a guarded document edit.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DiffOutcome.Applied), "applied")]
[JsonDerivedType(typeof(DiffOutcome.Stale), "stale")]
[JsonDerivedType(typeof(DiffOutcome.NotEditable), "not_editable")]
[JsonDerivedType(typeof(DiffOutcome.InvalidDiff), "invalid_diff")]
[JsonDerivedType(typeof(DiffOutcome.ThinContext), "thin_context")]
[JsonDerivedType(typeof(DiffOutcome.NoMatch), "no_match")]
[JsonDerivedType(typeof(DiffOutcome.Ambiguous), "ambiguous")]
[JsonDerivedType(typeof(DiffOutcome.OutOfOrder), "out_of_order")]
[JsonDerivedType(typeof(DiffOutcome.EmptyResult), "empty_result")]
[JsonDerivedType(typeof(DiffOutcome.TooLarge), "too_large")]
[JsonDerivedType(typeof(DiffOutcome.ApplyFailed), "apply_failed")]
public closed record DiffOutcome
{
    /// <summary>The edit produced a new document.</summary>
    public sealed record Applied(string Content, string NewHash) : DiffOutcome;

    /// <summary>The base hash no longer identifies current content.</summary>
    public sealed record Stale(string CurrentContent, string? CurrentHash) : DiffOutcome;

    /// <summary>A rejected edit.</summary>
    public closed record Rejected(string Detail) : DiffOutcome;

    /// <summary>The source owns this document.</summary>
    public sealed record NotEditable(LibraryDocumentSourceOrigin? Origin)
        : Rejected("Synchronized documents are not editable.");

    /// <summary>The input is not a supported diff.</summary>
    public sealed record InvalidDiff(string Detail) : Rejected(Detail);

    /// <summary>A hunk has insufficient context.</summary>
    public sealed record ThinContext(string Detail) : Rejected(Detail);

    /// <summary>A hunk's old side is absent.</summary>
    public sealed record NoMatch(string Detail) : Rejected(Detail);

    /// <summary>A hunk's old side occurs more than once.</summary>
    public sealed record Ambiguous(string Detail) : Rejected(Detail);

    /// <summary>Hunks are out of order.</summary>
    public sealed record OutOfOrder(string Detail) : Rejected(Detail);

    /// <summary>The resulting document has no content.</summary>
    public sealed record EmptyResult(string Detail) : Rejected(Detail);

    /// <summary>The resulting document exceeds the limit.</summary>
    public sealed record TooLarge(string Detail) : Rejected(Detail);

    /// <summary>The diff library could not apply the patch.</summary>
    public sealed record ApplyFailed(string Detail) : Rejected(Detail);
}
