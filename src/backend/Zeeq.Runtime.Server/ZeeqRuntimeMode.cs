namespace Zeeq.Runtime.Server;

/// <summary>
/// Reads process-level switches that select the runtime hosting mode.
/// </summary>
internal static class ZeeqRuntimeMode
{
    private const string RunModeVariable = "ZEEQ_RUN_MODE";
    private const string MessagingRoleVariable = "ZEEQ_MESSAGING_ROLE";

    /// <summary>
    /// Gets the current hosting mode.
    /// </summary>
    public static ZeeqRunMode Current =>
        Environment.GetEnvironmentVariable(RunModeVariable) is { } mode
        && string.Equals(mode, "worker", StringComparison.OrdinalIgnoreCase)
            ? ZeeqRunMode.Worker
            : ZeeqRunMode.Web;

    /// <summary>
    /// Gets the messaging role for the current process.
    /// </summary>
    public static ZeeqMessagingRuntimeRole MessagingRole =>
        Environment.GetEnvironmentVariable(MessagingRoleVariable) is { } role
            ? ParseMessagingRole(role)
            : throw new InvalidOperationException(
                $"Missing required messaging role. Set {MessagingRoleVariable} to producer, consumer, or producer-consumer."
            );

    private static ZeeqMessagingRuntimeRole ParseMessagingRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "producer" => ZeeqMessagingRuntimeRole.Producer,
            "consumer" => ZeeqMessagingRuntimeRole.Consumer,
            "producer-consumer" => ZeeqMessagingRuntimeRole.ProducerConsumer,
            _ => throw new InvalidOperationException(
                $"Unsupported messaging role '{role}'. Set {MessagingRoleVariable} to producer, consumer, or producer-consumer."
            ),
        };
}

/// <summary>
/// Runtime hosting modes supported by the server executable.
/// </summary>
internal enum ZeeqRunMode
{
    /// <summary>
    /// Starts the ASP.NET Core web host.
    /// </summary>
    Web,

    /// <summary>
    /// Starts the generic-host message worker without HTTP middleware.
    /// </summary>
    Worker,
}

/// <summary>
/// Messaging roles supported by a runtime process.
/// </summary>
internal enum ZeeqMessagingRuntimeRole
{
    /// <summary>
    /// Registers only message producers.
    /// </summary>
    Producer,

    /// <summary>
    /// Registers only message consumers.
    /// </summary>
    Consumer,

    /// <summary>
    /// Registers both message producers and message consumers.
    /// </summary>
    ProducerConsumer,
}
