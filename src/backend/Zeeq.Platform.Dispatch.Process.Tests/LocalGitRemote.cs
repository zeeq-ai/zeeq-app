using System.Diagnostics;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// A real local git repository used as a clone source in tests — no network
/// dependency, fully deterministic. Git accepts a local filesystem path as a
/// clone URL directly, so <see cref="Path"/> can be passed anywhere a
/// <c>RepoUrl</c> is expected.
/// </summary>
internal sealed class LocalGitRemote : IDisposable
{
    public LocalGitRemote()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"zeeq-git-remote-test-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);

        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
    }

    public string Path { get; }

    /// <summary>Writes a file and commits it, returning the new commit hash.</summary>
    public string Commit(string relativePath, string content, string message = "commit")
    {
        var absolutePath = System.IO.Path.Combine(Path, relativePath.TrimStart('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);

        RunGit("add", "-A");
        RunGit("commit", "-m", message);

        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {stderr}"
            );
        }

        return stdout;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
