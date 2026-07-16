using Zeeq.Core.Common;
using TUnit.Core;

namespace Zeeq.Testing;

/// <summary>
/// Skips tests in CI, where runtime settings force infrastructure-safe providers.
/// </summary>
/// <param name="reason">The reason for skipping the test.</param>
public sealed class SkipInCiAttribute(string reason) : SkipAttribute(reason)
{
    /// <summary>
    /// Returns true when CI-safe runtime settings require infrastructure tests to stay skipped.
    /// </summary>
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(RuntimeConfig.ForcePostgresMessaging);
}
