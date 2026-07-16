using Zeeq.Core.Common;
using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Creates Brighter GCP Pub/Sub gateway connections from Zeeq options.
/// </summary>
public static class GcpPubSubGatewayConnectionFactory
{
    /// <summary>
    /// Creates a gateway connection with optional emulator-aware Google builders.
    /// </summary>
    /// <param name="options">Transport options.</param>
    /// <returns>Configured Brighter GCP gateway connection.</returns>
    public static GcpMessagingGatewayConnection Create(GcpPubSubMessagingOptions options)
    {
        // NOTE: Use a fake access token when running in any environment that does
        // not have or does not need to have a real Google ADC available.  This is
        // safe since we are explicitly setting this up for Codespaces, Copilot
        // agents, and CI environments where we EXPLICITLY do not want to have
        // any ADC credentials.  `BypassGoogleAdc` requires `UseEmulatorDetection`
        // to be `true`.
        var credential =
            options.BypassGoogleAdc && RuntimeConfig.IsDevelopment
                ? GoogleCredential.FromAccessToken("local-fake")
                : GoogleCredential.GetApplicationDefault();

        var connection = new GcpMessagingGatewayConnection
        {
            ProjectId = options.ProjectId,
            Credential = credential,
        };

        if (!options.UseEmulatorDetection)
        {
            return connection;
        }

        connection.TopicManagerConfiguration = ConfigureEmulatorDetection;
        connection.SubscriptionManagerConfiguration = ConfigureEmulatorDetection;
        connection.PublisherConfiguration = ConfigureEmulatorDetection;
        connection.StreamConfiguration = ConfigureEmulatorDetection;

        return connection;
    }

    private static void ConfigureEmulatorDetection(PublisherServiceApiClientBuilder builder) =>
        builder.EmulatorDetection = EmulatorDetection.EmulatorOrProduction;

    private static void ConfigureEmulatorDetection(SubscriberServiceApiClientBuilder builder) =>
        builder.EmulatorDetection = EmulatorDetection.EmulatorOrProduction;

    private static void ConfigureEmulatorDetection(PublisherClientBuilder builder) =>
        builder.EmulatorDetection = EmulatorDetection.EmulatorOrProduction;

    private static void ConfigureEmulatorDetection(SubscriberClientBuilder builder) =>
        builder.EmulatorDetection = EmulatorDetection.EmulatorOrProduction;
}
