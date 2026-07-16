namespace Zeeq.Platform.Messaging;

/// <summary>
/// Exception thrown when discovered messaging metadata violates Zeeq conventions.
/// </summary>
public sealed class MessagingCatalogValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException(CreateMessage(errors))
{
    /// <summary>
    /// Validation errors that prevented catalog use.
    /// </summary>
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string CreateMessage(IReadOnlyList<string> errors) =>
        errors.Count == 0
            ? "Zeeq messaging catalog validation failed."
            : $"Zeeq messaging catalog validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}";
}
