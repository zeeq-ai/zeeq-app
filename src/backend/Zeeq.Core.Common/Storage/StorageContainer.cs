namespace Zeeq.Core.Common.Storage;

/// <summary>
/// Logical container for provider-backed storage objects.
/// </summary>
public enum StorageContainer
{
    /// <summary>Default private application object container.</summary>
    Default,

    /// <summary>Ephemeral diffs uploaded for local MCP expert code reviews.</summary>
    CodeReviewDiffs,
}
