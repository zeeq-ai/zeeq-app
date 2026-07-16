using Zeeq.Core.Common;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Google Cloud Pub/Sub transport options for Zeeq messaging.
/// </summary>
/// <remarks>
/// These options are intentionally transport-specific. Feature assemblies keep
/// using the shared Zeeq messaging attributes while this adapter maps the
/// discovered catalog to Brighter GCP Pub/Sub metadata.
///
/// <see cref="SubscriptionMode.Stream"/> uses Google Pub/Sub streaming pull: a
/// long-lived bidirectional gRPC stream delivers messages as they become
/// available. Brighter maps this to its stream consumer, which pushes messages
/// into an internal channel for the async message pump. This is the default
/// because it is the production-oriented path for steady consumers; configured
/// polling intervals are mostly inert because there is no repeated empty-poll
/// loop once the stream is open.
///
/// <see cref="SubscriptionMode.Pull"/> uses Pub/Sub unary pull calls: Brighter
/// asks the subscription for messages during each receive cycle. In this mode,
/// the generated timeout and empty-channel delay from
/// <see cref="ZeeqMessagingOptions"/> have practical effect because they
/// govern the pull wait and the delay after an empty pull. Pull mode is useful
/// for explicit emulator tests and constrained local scenarios, but it is not
/// the preferred high-throughput production mode.
///
/// Both modes are independent from Brighter's handler dispatch model. Generated
/// subscriptions still set <c>MessagePumpType.Proactor</c> so Zeeq's async
/// handlers run through Brighter's async pump in either Pub/Sub mode.
/// </remarks>
public sealed class GcpPubSubMessagingOptions
{
    /// <summary>
    /// Configuration section name for binding.
    /// </summary>
    public const string SectionName = "ZeeqMessaging:GcpPubSub";

    /// <summary>
    /// Google Cloud project id containing Pub/Sub topics and subscriptions.
    /// </summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// Missing topic and subscription provisioning policy.
    /// </summary>
    public GcpPubSubMissingChannelPolicy MissingChannelPolicy { get; init; } =
        GcpPubSubMissingChannelPolicy.Validate;

    /// <summary>
    /// Pub/Sub transport receive mode used by generated subscriptions.
    /// </summary>
    public SubscriptionMode SubscriptionMode { get; init; } = SubscriptionMode.Stream;

    /// <summary>
    /// Enables Pub/Sub ordering support on generated topics and subscriptions.
    /// </summary>
    /// <remarks>
    /// This remains disabled by default. It should only be enabled after the
    /// publisher path also supplies ordering keys.
    /// </remarks>
    public bool EnableMessageOrdering { get; init; }

    /// <summary>
    /// Enables Pub/Sub exactly-once delivery on generated subscriptions.
    /// </summary>
    public bool EnableExactlyOnceDelivery { get; init; }

    /// <summary>
    /// Optional transport-wide ack deadline override in seconds.
    /// </summary>
    /// <remarks>
    /// When unset, generated subscriptions derive the ack deadline from the
    /// resolved Zeeq visible timeout for the logical topic and consumer.
    /// </remarks>
    public int? AckDeadlineSeconds { get; init; }

    /// <summary>
    /// Labels applied to generated subscriptions.
    /// </summary>
    public Dictionary<string, string> Labels { get; init; } = [];

    /// <summary>
    /// Enables Google client emulator detection through Brighter builder hooks.
    /// </summary>
    public bool UseEmulatorDetection { get; init; } = true;

    /// <summary>
    /// Returns true when the application is running using emualtor detection,
    /// running in Codespaces, running in CI, or running as a Copilot Cloude Agent
    /// (Github Actions).  In this scenarios, we can bypass the Google ADC and use
    /// the mock token credentials safely for the application since it will be
    /// connected to the local emulator in all cases.
    /// </summary>
    public bool BypassGoogleAdc =>
        UseEmulatorDetection
        && (
            RuntimeConfig.IsCodespaces || RuntimeConfig.IsCI || RuntimeConfig.IsCopilotAgentRuntime
        );
}
