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
        Environment.GetEnvironmentVariable(RunModeVariable)?.Trim().ToLowerInvariant() switch
        {
            null or "" or "web" => ZeeqRunMode.Web,
            "worker" => ZeeqRunMode.Worker,
            "migrate" => ZeeqRunMode.Migrate,
            "db-cleanup" => ZeeqRunMode.DatabaseCleanup,
            "db-bootstrap" => ZeeqRunMode.DatabaseBootstrap,
            "verify-infrastructure" => ZeeqRunMode.VerifyInfrastructure,
            "topology" => ZeeqRunMode.Topology,
            _ => throw new InvalidOperationException(
                "Unsupported ZEEQ_RUN_MODE. Use web, worker, migrate, db-bootstrap, db-cleanup, verify-infrastructure, or topology."
            ),
        };

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

    /// <summary>Applies migrations and cache DDL without starting application services.</summary>
    Migrate,

    /// <summary>Prepares RDS roles and extensions.</summary>
    DatabaseBootstrap,

    /// <summary>Removes installation-owned SQL objects from a reused dedicated database.</summary>
    DatabaseCleanup,

    /// <summary>Verifies the restricted database and runtime AWS permissions without application credentials.</summary>
    VerifyInfrastructure,

    /// <summary>Exports the runtime queue catalog without accessing infrastructure.</summary>
    Topology,
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
