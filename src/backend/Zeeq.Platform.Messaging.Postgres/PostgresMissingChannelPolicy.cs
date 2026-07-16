using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Transport policy for handling missing Postgres queue tables.
/// </summary>
public enum OnMissingChannelPolicy
{
    /// <summary>
    /// Trust that migrations created the queue tables.
    /// </summary>
    Assume = 0,

    /// <summary>
    /// Validate that queue tables exist during Brighter startup.
    /// </summary>
    Validate = 1,

    /// <summary>
    /// Let Brighter create missing queue tables.
    /// </summary>
    Create = 2,
}

internal static class PostgresMissingChannelPolicyExtensions
{
    extension(OnMissingChannelPolicy policy)
    {
        public OnMissingChannel ToBrighter() =>
            policy switch
            {
                OnMissingChannelPolicy.Assume => OnMissingChannel.Assume,
                OnMissingChannelPolicy.Validate => OnMissingChannel.Validate,
                OnMissingChannelPolicy.Create => OnMissingChannel.Create,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    policy,
                    "Unknown channel policy."
                ),
            };
    }
}
