namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Pseudo-random worker identifier used to discriminate leases from different
/// processor instances.
/// </summary>
public static class ProcessingWorkerId
{
    /// <summary>
    /// Worker identity that stays constant for the lifetime of the process.
    /// </summary>
    public static readonly string Value = Guid.CreateVersion7().ToString("N");
}
