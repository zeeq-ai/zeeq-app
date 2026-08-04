using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zeeq.Core.Common;
using Zeeq.Platform.Telemetry.Processing;
using Zeeq.Platform.Telemetry.Setup;

namespace Zeeq.Platform.Telemetry.Tests;

public sealed class SetupTelemetryIngestExtensionTests
{
    [Test]
    public async Task AddTelemetryIngest_DoesNotRegisterRollupBackfillHostedService()
    {
        var services = new ServiceCollection();

        services.AddTelemetryIngest(new TelemetrySettings());

        await Assert.That(HasHostedService<AgentConversationRollupBackfillService>(services)).IsFalse();
    }

    [Test]
    public async Task AddAgentConversationRollupBackfill_RegistersRollupBackfillHostedService()
    {
        var services = new ServiceCollection();

        services.AddAgentConversationRollupBackfill();

        await Assert.That(HasHostedService<AgentConversationRollupBackfillService>(services)).IsTrue();
    }

    private static bool HasHostedService<THostedService>(IServiceCollection services)
        where THostedService : IHostedService =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(THostedService)
        );
}
