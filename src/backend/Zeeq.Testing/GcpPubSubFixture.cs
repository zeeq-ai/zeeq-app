using Testcontainers.PubSub;
using TUnit.Core.Interfaces;

namespace Zeeq.Testing;

/// <summary>
/// Test fixture for GCP Pub/Sub integration tests.
/// </summary>
public class GcpPubSubFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// The Pub/Sub container used for testing. This container is automatically
    /// started and stopped by the fixture.
    /// </summary>
    public PubSubContainer PubSubContainer { get; } =
        new PubSubBuilder(
            "gcr.io/google.com/cloudsdktool/google-cloud-cli:568.0.0-emulators"
        ).Build();

    /// <summary>
    /// Disposes the Pub/Sub container.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await PubSubContainer.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    async Task IAsyncInitializer.InitializeAsync()
    {
        await PubSubContainer.StartAsync().ConfigureAwait(false);
    }
}
