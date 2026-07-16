using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Runs small local processes needed by the Aspire AppHost.
/// </summary>
/// <remarks>
/// The AppHost uses this to query CSharpRepl for startup-hook environment
/// variables before launching the development server resource. Keeping the
/// shell-out here avoids enabling the REPL connector for unrelated dotnet
/// processes in the user's directory shell.
/// See: https://fuqua.io/blog/2026/06/injecting-a-csharp-repl-into-a-running-net-process/
/// </remarks>
internal static class ProcessUtils
{
    /// <summary>
    /// Attempts to get CSharpRepl connector environment variables for a child process.
    /// </summary>
    /// <remarks>
    /// Missing tools or invalid connector output should not prevent local
    /// Aspire startup. The AppHost logs the skip and continues without REPL
    /// attachment when setup cannot be resolved.
    /// See: https://www.nuget.org/packages/CSharpRepl
    /// </remarks>
    public static IReadOnlyDictionary<string, string> TryGetCSharpReplConnectEnvironment()
    {
        try
        {
            return GetCSharpReplConnectEnvironment();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Skipping CSharpRepl connect setup: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Runs <c>csharprepl connect init</c> and parses its shell exports.
    /// </summary>
    /// <remarks>
    /// The command prints startup-hook values as shell <c>export</c> statements.
    /// The AppHost converts those into environment variables attached only to
    /// the opted-in Aspire project resource.
    /// See: https://fuqua.io/blog/2026/06/injecting-a-csharp-repl-into-a-running-net-process/
    /// </remarks>
    private static IReadOnlyDictionary<string, string> GetCSharpReplConnectEnvironment()
    {
        var startInfo = new ProcessStartInfo("csharprepl", "connect init")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start csharprepl connect init.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("csharprepl connect init timed out.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"csharprepl connect init failed with exit code {process.ExitCode}: {error}"
            );
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("export ", StringComparison.Ordinal))
            .Select(ParseShellExport)
            .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Parses a single <c>export NAME="VALUE"</c> line emitted by CSharpRepl.
    /// </summary>
    /// <remarks>
    /// NOTE: Strictness here is deliberate, not an oversight. The tool version is
    /// pinned by mise (<c>dotnet:CSharpRepl</c> in <c>.config/mise.toml</c>), so the
    /// output shape is stable, and a parse failure surfaces as a logged skip in
    /// <see cref="TryGetCSharpReplConnectEnvironment"/> — never a startup crash.
    /// Failing loudly on an unexpected line beats silently exporting a malformed
    /// startup-hook value that would break REPL attachment in a confusing way later.
    /// </remarks>
    private static (string Name, string Value) ParseShellExport(string line)
    {
        const string prefix = "export ";

        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected csharprepl export line: {line}");
        }

        var assignment = line[prefix.Length..];
        var separatorIndex = assignment.IndexOf('=', StringComparison.Ordinal);

        if (separatorIndex < 1)
        {
            throw new InvalidOperationException($"Unexpected csharprepl export line: {line}");
        }

        var name = assignment[..separatorIndex];
        var value = assignment[(separatorIndex + 1)..].Trim();

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            throw new InvalidOperationException($"Unexpected csharprepl export value: {line}");
        }

        return (name, value[1..^1]);
    }
}
