namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Identifies the world-model worker that owns a scheduler target namespace.
/// </summary>
public enum WorldModelWorkConsumer
{
    /// <summary>Proposal curation and world-model mutation work.</summary>
    Curator = 1,

    /// <summary>Semantic embedding and cluster-index maintenance work.</summary>
    ClusterIndex = 2,
}
