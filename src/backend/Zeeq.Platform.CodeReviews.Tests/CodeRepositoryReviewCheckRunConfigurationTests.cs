using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Verifies check-run configuration evaluation rules.
///
/// dotnet run --output detailed --disable-logo --treenode-filter "/*/*/CodeRepositoryReviewCheckRunConfigurationTests/*"
/// </summary>
public sealed class CodeRepositoryReviewCheckRunConfigurationTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task IsEnabled_ReflectsSelectedSeverities(
        bool blockOnCritical,
        bool blockOnMajor,
        bool expected
    )
    {
        var config = new CodeRepositoryReviewCheckRunConfiguration
        {
            BlockOnCritical = blockOnCritical,
            BlockOnMajor = blockOnMajor,
        };

        await Assert.That(config.IsEnabled).IsEqualTo(expected);
    }

    [Test]
    public async Task EmptyConfiguration_IsDisabled()
    {
        await Assert.That(CodeRepositoryReviewCheckRunConfiguration.Empty.IsEnabled).IsFalse();
    }

    // ── ShouldBlock truth table ────────────────────────────────────────

    [Test]
    [Arguments(false, false, 0, 0, false)]
    [Arguments(false, false, 1, 0, false)]
    [Arguments(false, false, 0, 1, false)]
    [Arguments(false, false, 1, 1, false)]
    [Arguments(true, false, 0, 0, false)]
    [Arguments(true, false, 1, 0, true)]
    [Arguments(true, false, 0, 1, false)]
    [Arguments(true, false, 1, 1, true)]
    [Arguments(false, true, 0, 0, false)]
    [Arguments(false, true, 1, 0, true)]
    [Arguments(false, true, 0, 1, true)]
    [Arguments(false, true, 1, 1, true)]
    [Arguments(true, true, 0, 0, false)]
    [Arguments(true, true, 1, 0, true)]
    [Arguments(true, true, 0, 1, true)]
    [Arguments(true, true, 1, 1, true)]
    public async Task ShouldBlock_MatchesTruthTable(
        bool blockOnCritical,
        bool blockOnMajor,
        int criticalFindings,
        int majorFindings,
        bool expected
    )
    {
        var config = new CodeRepositoryReviewCheckRunConfiguration
        {
            BlockOnCritical = blockOnCritical,
            BlockOnMajor = blockOnMajor,
        };

        await Assert.That(config.ShouldBlock(criticalFindings, majorFindings)).IsEqualTo(expected);
    }
}
