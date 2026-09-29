using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Processing;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Cost enricher tests covering token estimation, nano-AIU conversion, and
/// pass-through cases.
/// </summary>
[Category("Unit")]
public sealed class CostEnricherTests
{
    private readonly IAgentTelemetryCostEnricher _enricher = new AgentTelemetryCostEnricher();

    [Test]
    [Arguments("gpt-6.1-sol", 2.00, 0.10, 10.00)]
    [Arguments("gpt-6-astra", 10.00, 1.00, 50.00)]
    [Arguments("gpt-6-sol", 2.00, 0.20, 10.00)]
    [Arguments("gpt-6-luna", 0.10, 0.01, 0.50)]
    [Arguments("gpt-5.6-sol", 4.00, 0.40, 20.00)]
    [Arguments("gpt-5.6-cyber", 12.50, 1.25, 75.00)]
    [Arguments("gpt-5.5-cyber", 12.50, 1.25, 75.00)]
    [Arguments("claude-opus-5.5", 4.00, 0.20, 20.00)]
    [Arguments("claude-sonnet-5.5", 2.00, 0.20, 10.00)]
    [Arguments("claude-fable-5.1", 10.00, 0.25, 50.00)]
    [Arguments("claude-mythos-5.1", 10.00, 0.25, 50.00)]
    public async Task CurrentModels_ChargeEachTokenCategoryAtPublishedRates(
        string model,
        double inputPerMillion,
        double cachedPerMillion,
        double outputPerMillion
    )
    {
        // Guards the published rates independently, including reduced cache-read multipliers.
        // Use short-context requests so the baseline estimate applies to actual provider pricing.
        var input = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            Model: model,
            InputTokens: 100_000
        );
        var cached = input with { CachedTokens = 100_000 };
        var output = input with { InputTokens = 0, OutputTokens = 100_000 };

        var inputResult = _enricher.Enrich(input, "zeeq-agent");
        var cachedResult = _enricher.Enrich(cached, "zeeq-agent");
        var outputResult = _enricher.Enrich(output, "zeeq-agent");

        await Assert.That(inputResult.CostUsd).IsEqualTo((decimal)inputPerMillion / 10m);
        await Assert.That(cachedResult.CostUsd).IsEqualTo((decimal)cachedPerMillion / 10m);
        await Assert.That(outputResult.CostUsd).IsEqualTo((decimal)outputPerMillion / 10m);
        await Assert
            .That(cachedResult.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.EstimatedFromTokens);
    }

    [Test]
    public async Task NonCompletionEvent_PassesThrough()
    {
        var evt = new AgentSessionEventRecord(AgentSessionEventType.Prompt);

        var result = _enricher.Enrich(evt, "codex");

        await Assert.That(result.CostUsd).IsNull();
        await Assert.That(result.CostSource).IsNull();
    }

    [Test]
    public async Task AlreadyCosted_PassesThrough()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            CostUsd: 0.05m,
            CostSource: AgentSessionEventCostSource.ReportedUsd
        );

        var result = _enricher.Enrich(evt, "claude-code");

        await Assert.That(result.CostUsd).IsEqualTo(0.05m);
        await Assert.That(result.CostSource).IsEqualTo(AgentSessionEventCostSource.ReportedUsd);
    }

    [Test]
    public async Task Codex_EstimatesFromTokens()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            Model: "gpt-5.4",
            InputTokens: 10000,
            CachedTokens: 5000,
            OutputTokens: 2000
        );

        var result = _enricher.Enrich(evt, "codex");

        await Assert
            .That(result.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.EstimatedFromTokens);
        await Assert.That(result.CostUsd).IsNotNull();
        // InputTokens may include cached; defensive subtraction: regularInput = 10000 - 5000 = 5000
        var expected = (5000 * 2.50m + 5000 * 0.25m + 2000 * 15.00m) / 1_000_000m;
        await Assert.That(result.CostUsd!.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task Copilot_WithCachedTokens_ChargesRegularAndCachedInputSeparately()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            Model: "claude-sonnet-5",
            InputTokens: 1200,
            CachedTokens: 200,
            OutputTokens: 100
        );

        var result = _enricher.Enrich(evt, "copilot-chat");

        // InputTokens is normalized to total input: 1000 regular + 200 cached tokens.
        var expected = (1000 * 2.00m + 200 * 0.20m + 100 * 10.00m) / 1_000_000m;

        await Assert.That(result.CostUsd).IsEqualTo(expected);
        await Assert
            .That(result.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.EstimatedFromTokens);
    }

    [Test]
    public async Task Copilot_WithTokens_EstimatesFromTokens()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            Model: "claude-sonnet-5",
            InputTokens: 5000,
            OutputTokens: 800,
            CostUnitsRaw: 3_500_000
        );

        var result = _enricher.Enrich(evt, "copilot-chat");

        await Assert
            .That(result.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.EstimatedFromTokens);
        await Assert.That(result.CostUsd).IsNotNull();
        // claude-sonnet-5: $2.00/$10.00 per 1M tokens
        var expected = (5000 * 2.00m + 800 * 10.00m) / 1_000_000m;
        await Assert.That(result.CostUsd!.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task Copilot_WithoutTokens_UsesNanoAiu()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            CostUnitsRaw: 5_000_000
        );

        var result = _enricher.Enrich(evt, "copilot-chat");

        await Assert.That(result.CostSource).IsEqualTo(AgentSessionEventCostSource.BillingUnits);
        await Assert.That(result.CostUsd).IsNotNull();
        await Assert.That(result.CostUsd!.Value).IsEqualTo(0.05m); // 5M * 1e-8
    }

    [Test]
    public async Task Copilot_WithoutModelAndTokens_UsesNanoAiu()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            CostUnitsRaw: 10_000_000
        );

        var result = _enricher.Enrich(evt, "copilot-chat");

        await Assert.That(result.CostSource).IsEqualTo(AgentSessionEventCostSource.BillingUnits);
        await Assert.That(result.CostUsd).IsEqualTo(0.10m);
    }

    [Test]
    public async Task TokenlessCompletion_PassesThrough()
    {
        var evt = new AgentSessionEventRecord(AgentSessionEventType.Completion);

        var result = _enricher.Enrich(evt, "codex");

        await Assert.That(result.CostUsd).IsNull();
        await Assert.That(result.CostSource).IsNull();
    }

    [Test]
    public async Task UnknownModel_FallsBackToDefaultRates()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            Model: "some-future-model",
            InputTokens: 10000,
            OutputTokens: 1000
        );

        var result = _enricher.Enrich(evt, "codex");

        await Assert
            .That(result.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.EstimatedFromTokens);
        var expected = (10000 * 2.50m + 1000 * 15.00m) / 1_000_000m; // default rates
        await Assert.That(result.CostUsd!.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task UnknownHarness_PassesThrough()
    {
        var evt = new AgentSessionEventRecord(
            AgentSessionEventType.Completion,
            InputTokens: 1000,
            OutputTokens: 500
        );

        var result = _enricher.Enrich(evt, "cursor");

        await Assert.That(result.CostUsd).IsNull();
        await Assert.That(result.CostSource).IsNull();
    }
}
