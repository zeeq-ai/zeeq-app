using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Transport policy for handling missing Pub/Sub topics and subscriptions.
/// </summary>
public enum GcpPubSubMissingChannelPolicy
{
    /// <summary>
    /// Trust that topics and subscriptions already exist.
    /// </summary>
    Assume = 0,

    /// <summary>
    /// Validate that topics and subscriptions exist during Brighter startup.
    /// </summary>
    Validate = 1,

    /// <summary>
    /// Let Brighter create missing topics and subscriptions.
    /// </summary>
    Create = 2,
}

/// <summary>
/// Converts Zeeq Pub/Sub provisioning policy values to Brighter channel policy values.
/// </summary>
/// <remarks>
/// The adapter keeps a Zeeq-owned enum so configuration does not expose
/// Brighter-specific type names. This internal conversion is the only place
/// that policy crosses into Brighter metadata.
/// </remarks>
internal static class GcpPubSubMissingChannelPolicyExtensions
{
    extension(GcpPubSubMissingChannelPolicy policy)
    {
        /// <summary>
        /// Maps the Zeeq Pub/Sub policy to Brighter's <see cref="OnMissingChannel"/> value.
        /// </summary>
        /// <returns>Equivalent Brighter missing-channel policy.</returns>
        public OnMissingChannel ToBrighter() =>
            policy switch
            {
                GcpPubSubMissingChannelPolicy.Assume => OnMissingChannel.Assume,
                GcpPubSubMissingChannelPolicy.Validate => OnMissingChannel.Validate,
                GcpPubSubMissingChannelPolicy.Create => OnMissingChannel.Create,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    policy,
                    "Unknown Pub/Sub missing channel policy."
                ),
            };
    }
}
