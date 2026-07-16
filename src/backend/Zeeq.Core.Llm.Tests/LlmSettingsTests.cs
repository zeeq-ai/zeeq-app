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
}
