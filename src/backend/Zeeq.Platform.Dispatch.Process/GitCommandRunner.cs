using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Dispatch.Process;

/// <summary>
/// Runs <c>git</c> as a subprocess, piping stdout/stderr to the logger and
/// honoring cancellation.
/// </summary>
/// <remarks>
/// All git invocations go through this one seam so the token-redaction and
/// cancellation-to-kill behavior is enforced in exactly one place. Arguments
/// are always passed via <see cref="ProcessStartInfo.ArgumentList"/> rather
/// than a shell command string — no argument is ever shell-interpreted, so a
/// token or a repository URL containing shell metacharacters cannot break out
/// of its argument position.
/// </remarks>
internal sealed partial class GitCommandRunner(ILogger<GitCommandRunner> logger)
{
    /// <summary>Runs a git command, discarding stdout (routed to the logger) and throwing on a nonzero exit code.</summary>
    public async Task RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        await ExecuteAsync(workingDirectory, arguments, captureOutput: false, cancellationToken);
    }

    /// <summary>Runs a git command and returns its trimmed stdout, throwing on a nonzero exit code.</summary>
    public async Task<string> CaptureOutputAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var (_, stdout) = await ExecuteAsync(
            workingDirectory,
            arguments,
            captureOutput: true,
            cancellationToken
        );

        return stdout.Trim();
    }

    private async Task<(int ExitCode, string Stdout)> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool captureOutput,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var stdoutBuffer = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (captureOutput)
            {
                stdoutBuffer.AppendLine(e.Data);
            }

            LogGitOutput(logger, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                LogGitOutput(logger, e.Data);
            }
        };

        LogGitCommand(logger, workingDirectory, Redact(arguments));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // NOTE: WaitForExitAsync alone does not kill the process on
        // cancellation — it just stops awaiting. A cancelled ingest run must
        // not leave an orphaned git process holding the workspace directory
        // open, so cancellation explicitly kills the process tree.
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill — benign race.
            }
        });

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new GitCommandException(
                $"git {string.Join(' ', Redact(arguments))} exited with code {process.ExitCode} in {workingDirectory}"
            );
        }

        return (process.ExitCode, stdoutBuffer.ToString());
    }

    /// <summary>
    /// Masks the value of every <c>-c &lt;key&gt;=&lt;value&gt;</c> argument
    /// before it reaches a log line or an exception message.
    /// </summary>
    /// <remarks>
    /// Redacts by argument position (immediately follows a <c>-c</c> flag)
    /// rather than by matching the current credential's content
    /// (<c>"Authorization:"</c>). The only <c>-c</c> usage anywhere in this
    /// codebase's git invocations is <see cref="LocalTempWorkspaceProvider.AuthConfigArgs"/>
    /// injecting the token — but redacting by position means any future
    /// <c>-c</c> usage (a different auth header shape, a different credential
    /// mechanism) is masked automatically instead of depending on someone
    /// remembering to update a substring match.
    /// </remarks>
    private static IReadOnlyList<string> Redact(IReadOnlyList<string> arguments)
    {
        var redacted = new List<string>(arguments.Count);

        for (var i = 0; i < arguments.Count; i++)
        {
            redacted.Add(arguments[i]);

            if (arguments[i] == "-c" && i + 1 < arguments.Count)
            {
                redacted.Add("[REDACTED]");
                i++;
            }
        }

        return redacted;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "git {WorkingDirectory}$ git {Arguments}")]
    private static partial void LogGitCommand(
        ILogger logger,
        string workingDirectory,
        IReadOnlyList<string> arguments
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Line}")]
    private static partial void LogGitOutput(ILogger logger, string line);
}

/// <summary>Thrown when a git subprocess exits with a nonzero code.</summary>
public sealed class GitCommandException(string message) : Exception(message);
