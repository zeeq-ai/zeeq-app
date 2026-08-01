using Microsoft.Extensions.AI;
using Zeeq.Core.Common;

namespace Zeeq.Core.Llm.Tests;

/// <summary>
/// Unit tests for shared LLM settings behavior.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Llm.Tests --output detailed --disable-logo
/// </summary>
public sealed class LlmSettingsTests
{
    [Test]
    public async Task LlmModelDefaults_WithOnlyFastApiKey_UsesFastForHighAndMax()
    {
        var settings = new LlmModelDefaults
        {
            Fast = new LlmModelDefault { ApiKey = "fast-key", Model = "fast-model" },
            High = new LlmModelDefault { Model = "high-model" },
            Max = new LlmModelDefault { Model = "max-model" },
        };

        await Assert.That(settings.Fast.ApiKey).IsEqualTo("fast-key");
        await Assert.That(settings.High.ApiKey).IsEqualTo("fast-key");
        await Assert.That(settings.Max.ApiKey).IsEqualTo("fast-key");
    }

    [Test]
    public async Task LlmModelDefaults_WithHighAndMaxApiKeys_UsesTierSpecificKeys()
    {
        var settings = new LlmModelDefaults
        {
            Fast = new LlmModelDefault { ApiKey = "fast-key", Model = "fast-model" },
            High = new LlmModelDefault { ApiKey = "high-key", Model = "high-model" },
            Max = new LlmModelDefault { ApiKey = "max-key", Model = "max-model" },
        };

        await Assert.That(settings.High.ApiKey).IsEqualTo("high-key");
        await Assert.That(settings.Max.ApiKey).IsEqualTo("max-key");
    }

    [Test]
    public async Task LlmUsageSink_Add_AccumulatesCachedInputTokens()
    {
        var sink = new LlmUsageSink();

        sink.Add(
            new UsageDetails
            {
                InputTokenCount = 100,
                CachedInputTokenCount = 64,
                OutputTokenCount = 20,
                TotalTokenCount = 120,
            }
        );
        sink.Add(
            new UsageDetails
            {
                InputTokenCount = 50,
                CachedInputTokenCount = 16,
                OutputTokenCount = 10,
                TotalTokenCount = 60,
            }
        );

        await Assert.That(sink.InputTokens).IsEqualTo(150);
        await Assert.That(sink.CachedInputTokens).IsEqualTo(80);
        await Assert.That(sink.OutputTokens).IsEqualTo(30);
        await Assert.That(sink.TotalTokens).IsEqualTo(180);
        await Assert.That(sink.InputTokensOrNull).IsEqualTo(150);
        await Assert.That(sink.CachedInputTokensOrNull).IsEqualTo(80);
        await Assert.That(sink.OutputTokensOrNull).IsEqualTo(30);
        await Assert.That(sink.TotalTokensOrNull).IsEqualTo(180);
        await Assert.That(sink.HasUsage).IsTrue();
        await Assert.That(sink.HasTotalTokens).IsTrue();
    }

    [Test]
    public async Task LlmUsageSink_Add_PreservesPerFieldPresence()
    {
        var sink = new LlmUsageSink();

        sink.Add(new UsageDetails { CachedInputTokenCount = 0 });

        await Assert.That(sink.HasUsage).IsTrue();
        await Assert.That(sink.HasCachedInputTokens).IsTrue();
        await Assert.That(sink.CachedInputTokens).IsEqualTo(0);
        await Assert.That(sink.CachedInputTokensOrNull).IsEqualTo(0);
        await Assert.That(sink.HasInputTokens).IsFalse();
        await Assert.That(sink.InputTokensOrNull).IsNull();
        await Assert.That(sink.HasOutputTokens).IsFalse();
        await Assert.That(sink.OutputTokensOrNull).IsNull();
        await Assert.That(sink.HasTotalTokens).IsFalse();
        await Assert.That(sink.TotalTokensOrNull).IsNull();
    }
}
