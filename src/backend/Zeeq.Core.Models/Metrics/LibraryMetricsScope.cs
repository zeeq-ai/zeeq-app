namespace Zeeq.Core.Models;

/// <summary>Library names visible to the caller of a library-scoped dashboard request.</summary>
public sealed class LibraryMetricsScope
{
    /// <summary>Null indicates unrestricted access. An empty array hides all library-tagged metrics.</summary>
    public string[]? VisibleNames { get; set; }
}
