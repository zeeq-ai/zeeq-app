using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Tests for <see cref="GitCommandRunner"/> against a real local git repo — no
/// network dependency, exercises the actual subprocess plumbing.
///
/// dotnet run --project src/backend/Zeeq.Platform.Dispatch.Process.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitCommandRunnerTests/*"
/// </summary>
public sealed class GitCommandRunnerTests
{
    [Test]
    public async Task RunAsync_ValidCommand_Succeeds()
    {
        using var remote = new LocalGitRemote();
        remote.Commit("README.md", "# Hello");

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        await runner.RunAsync(remote.Path, ["log", "--oneline"], CancellationToken.None);
    }

    [Test]
    public async Task RunAsync_InvalidCommand_ThrowsGitCommandException()
    {
        using var remote = new LocalGitRemote();
        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        await Assert
            .That(async () =>
                await runner.RunAsync(
                    remote.Path,
                    ["not-a-real-git-subcommand"],
                    CancellationToken.None
                )
            )
            .Throws<GitCommandException>();
    }

    [Test]
    public async Task CaptureOutputAsync_ReturnsTrimmedStdout()
    {
        using var remote = new LocalGitRemote();
        var commitHash = remote.Commit("README.md", "# Hello");

        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        var output = await runner.CaptureOutputAsync(
            remote.Path,
            ["rev-parse", "HEAD"],
            CancellationToken.None
        );

        await Assert.That(output).IsEqualTo(commitHash);
    }

    [Test]
    public async Task RunAsync_FailingCommandWithDashCArg_RedactsValueFromExceptionMessage()
    {
        using var remote = new LocalGitRemote();
        var runner = new GitCommandRunner(NullLogger<GitCommandRunner>.Instance);

        GitCommandException? caught = null;

        try
        {
            await runner.RunAsync(
                remote.Path,
                [
                    "-c",
                    "http.extraHeader=Authorization: Bearer secret-token-value",
                    "not-a-real-git-subcommand",
                ],
                CancellationToken.None
            );
        }
        catch (GitCommandException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("[REDACTED]");
        await Assert.That(caught.Message).DoesNotContain("secret-token-value");
    }
}
