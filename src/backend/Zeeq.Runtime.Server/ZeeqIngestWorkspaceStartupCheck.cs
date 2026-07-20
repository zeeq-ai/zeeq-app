namespace Zeeq.Runtime.Server;

/// <summary>
/// Synthetic write/read/delete check against the configured ingest workspace
/// root, run once at worker startup before message consumption begins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> the worker's ingest workspace root
/// (<see cref="IngestSettings.ContentRootPath"/>) is a mounted Cloud Run
/// ephemeral disk in production (see
/// <c>docs/content/5.configuration/1-gcp-runtime.md</c>), not local disk —
/// a wrong mount path, a missing/misconfigured volume, or an IAM permission
/// gap would otherwise go undetected until the first real
/// customer's ingest job fails deep inside a background message handler,
/// with a much less obvious error than "the mount doesn't work." This check
/// exercises the filesystem operations the mount must support
/// (create/read/delete) against the real configured root, so a broken mount
/// fails loud at startup instead of silently on the first real job.
/// </para>
/// <para>
/// Registered only by <see cref="ZeeqWorkerHost"/>, since only the worker
/// runs the ingest pipeline. Runs unconditionally (including local dev
/// against the OS temp directory) rather than only when a non-default
/// <c>ContentRootPath</c> is configured, so the same code path is always
/// exercised — no separate "am I in production" branch to keep correct.
/// </para>
/// <para>
/// <b>Fails fast on purpose:</b> throwing from <see cref="StartAsync"/>
/// fails Generic Host startup itself, so the process exits before any
/// message consumer starts. On Cloud Run, that surfaces as a crash-looping
/// worker instance — loud and immediately visible, rather than a healthy-
/// looking worker that silently fails every ingest job it picks up.
/// </para>
/// </remarks>
internal sealed partial class ZeeqIngestWorkspaceStartupCheck(
    AppSettings appSettings,
    ILogger<ZeeqIngestWorkspaceStartupCheck> logger
) : IHostedService
{
    private const string ProbeContent = "zeeq-ingest-workspace-startup-check";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(appSettings.Ingest.ContentRootPath)
            ? Path.GetTempPath()
            : appSettings.Ingest.ContentRootPath;

        // A probe file at the mount root, not under public/private — this
        // check is purely "can this process create/read/delete a file here
        // at all," independent of the ingest path scheme.
        var probePath = Path.Combine(root, $".zeeq-startup-check-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            await File.WriteAllTextAsync(probePath, ProbeContent, cancellationToken);
            var readBack = await File.ReadAllTextAsync(probePath, cancellationToken);

            if (readBack != ProbeContent)
            {
                throw new InvalidOperationException(
                    $"Ingest workspace startup check read back unexpected content at '{probePath}'."
                );
            }

            File.Delete(probePath);

            LogStartupCheckPassed(logger, root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartupCheckFailed(logger, root, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "✅  Ingest workspace startup check passed. Root={Root}"
    )]
    private static partial void LogStartupCheckPassed(
        Microsoft.Extensions.Logging.ILogger logger,
        string root
    );

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "❌  Ingest workspace startup check FAILED — the worker cannot create/read/delete files under Root={Root}. Refusing to start."
    )]
    private static partial void LogStartupCheckFailed(
        Microsoft.Extensions.Logging.ILogger logger,
        string root,
        Exception ex
    );
}
