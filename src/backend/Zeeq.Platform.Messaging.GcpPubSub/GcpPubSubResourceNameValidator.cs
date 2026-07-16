namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Validates generated Pub/Sub topic and subscription ids.
/// </summary>
/// <remarks>
/// Zeeq fails fast during metadata generation instead of allowing Brighter or
/// Google client calls to discover bad resource names after startup begins.
/// </remarks>
public sealed class GcpPubSubResourceNameValidator
{
    private const int MinLength = 3;
    private const int MaxLength = 255;

    /// <summary>
    /// Validates a generated Pub/Sub topic id.
    /// </summary>
    /// <param name="topicId">Topic id without the project prefix.</param>
    public void ValidateTopic(string topicId)
    {
        Validate(topicId);
    }

    /// <summary>
    /// Validates a generated Pub/Sub subscription id.
    /// </summary>
    /// <param name="subscriptionId">Subscription id without the project prefix.</param>
    public void ValidateSubscription(string subscriptionId)
    {
        Validate(subscriptionId);
    }

    private static void Validate(string resourceId)
    {
        if (resourceId.Length is < MinLength or > MaxLength)
        {
            throw new GcpPubSubResourceNameException(
                resourceId,
                "length must be between 3 and 255 characters"
            );
        }

        if (!IsAsciiLetter(resourceId[0]))
        {
            throw new GcpPubSubResourceNameException(
                resourceId,
                "first character must be a letter"
            );
        }

        if (resourceId.StartsWith("goog", StringComparison.OrdinalIgnoreCase))
        {
            throw new GcpPubSubResourceNameException(
                resourceId,
                "resource id must not start with 'goog'"
            );
        }

        if (resourceId.Any(character => !IsAllowedCharacter(character)))
        {
            throw new GcpPubSubResourceNameException(
                resourceId,
                "allowed characters are letters, digits, '-', '.', '_', '~', '+', and '%'"
            );
        }
    }

    private static bool IsAllowedCharacter(char character) =>
        IsAsciiLetter(character)
        || char.IsAsciiDigit(character)
        || character is '-' or '.' or '_' or '~' or '+' or '%';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
