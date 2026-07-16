using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Dispatch.Process;

/// <summary>
/// Acquires ingest workspaces on local temp disk by cloning (or pulling) the
/// job's repository with <c>git</c>.
/// </summary>
/// <remarks>
/// Implements spec §5.2 (deterministic path scheme) and §5.3 (clone-or-pull,
/// partial clone, shallow depth, sparse checkout, bounded retry, token
/// injection). This is the v1 default workspace provider — the local-disk
/// counterpart to the future <c>MountedVolumeWorkspaceProvider</c> (spec
/// §11.2/Phase 3), which will reuse the same path scheme and git orchestration
/// against a GCS-mounted root instead of <see cref="AppSettings.Ingest"/>'s
/// default OS temp directory.
/// <para>
/// <b>Sparse-checkout scope, a deliberate simplification:</b> the git-level
/// sparse-checkout pattern set is always just the three Markdown extensions
/// (<c>*.md</c>, <c>*.mdc</c>, <c>*.mdx</c>) — it does <i>not</i> also encode
/// <see cref="EffectiveFilter.IncludeGlobs"/>/<c>ExcludeGlobs</c>. Translating
/// arbitrary include/exclude globs into git's sparse-checkout pattern dialect
/// correctly (cone vs. non-cone semantics, directory vs. file patterns) is a
/// second glob dialect to get right for a benefit that's bandwidth only —
/// correctness is unaffected either way, because
/// <c>Zeeq.Platform.Ingest.IngestFileFilter</c> is the single source of
/// truth for which files are actually ingested and already enforces the full
/// effective filter at read time. Worst case with this simplification: a
/// narrowly-filtered public source's sparse checkout materializes more
/// Markdown files on disk than strictly necessary; it never materializes
/// fewer than what correctness requires. Revisit if a repository's full
/// Markdown tree becomes large enough that this slack matters.
/// </para>
/// </remarks>
internal sealed partial class LocalTempWorkspaceProvider(
    AppSettings appSettings,
    IIngestGitHubTokenProvider tokenProvider,
    GitCommandRunner git,
    ILogger<LocalTempWorkspaceProvider> logger
) : IIngestWorkspaceProvider
{
    private const int MaxAcquireAttempts = 2;
    private static readonly string[] MarkdownSparsePatterns = ["*.md", "*.mdc", "*.mdx"];

    /// <summary>
    /// Per-invocation <c>-c safe.directory=*</c> — see
    /// <see cref="RunGitAsync"/>/<see cref="CaptureGitOutputAsync"/> for why
    /// every git call in this class goes through one of those two wrappers
    /// instead of calling <see cref="GitCommandRunner"/> directly.
    /// </summary>
    private static readonly string[] SafeDirectoryConfigArgs = ["-c", "safe.directory=*"];

    /// <inheritdoc />
    public async Task<IIngestWorkspace> AcquireAsync(
        RepositoryIngestJob job,
        CancellationToken cancellationToken
    )
    {
        var path = ResolveWorkspacePath(job);
        var token = await tokenProvider.GetTokenAsync(job, cancellationToken);

        await CloneOrPullWithRetryAsync(path, job.RepoUrl, token, cancellationToken);

        return new LocalIngestWorkspace(path, job.Kind);
    }

    /// <summary>
    /// Runs a git command with <c>-c safe.directory=*</c> applied, so git
    /// doesn't refuse to operate under a mounted workspace root it doesn't
    /// recognize as owned by the current user.
    /// </summary>
    /// <remarks>
    /// <b>Why this is needed at all:</b> git 2.35+ refuses any operation
    /// inside a directory whose owner uid doesn't match the running
    /// process's uid ("detected dubious ownership") — a defense against a
    /// different local user planting a malicious repo. On the mounted-GCS
    /// production deployment (Cloud Storage FUSE volume, see
    /// <c>docs/content/5.configuration/1-gcp-runtime.md</c>), Google's own
    /// docs state volumes are root-owned by default; our container image
    /// also has no <c>USER</c> directive (confirmed via
    /// <c>docker inspect --format '{{.Config.User}}'</c> against the
    /// published base image — empty, i.e. root), so ownership likely already
    /// matches today. This is configured defensively anyway rather than
    /// relying on that alignment holding forever — a future base-image
    /// change to a non-root default, or a GCS FUSE mount option change,
    /// would otherwise silently break every ingest run in production with no
    /// local-dev signal (local temp disk is always already owned by the
    /// current user, so this path is never exercised outside a mounted
    /// deployment).
    /// <para>
    /// <b>Per-invocation <c>-c</c>, not <c>git config --global --add</c>.</b>
    /// A <c>--global</c> write mutates the real, persistent gitconfig file
    /// for whatever user runs this process — on a developer machine or a CI
    /// runner (not just the production container), that would silently
    /// disable git's ownership check for <i>every other</i> git repository
    /// that same user ever touches, well beyond this pipeline's own
    /// workspaces, and it would never be cleaned up. Passing
    /// <c>-c safe.directory=*</c> as a per-invocation argument instead
    /// leaves zero persistent state anywhere: the override only exists for
    /// the lifetime of that one subprocess call.
    /// </para>
    /// <para>
    /// <b>Why <c>*</c> instead of the specific mounted root:</b> git's
    /// <c>safe.directory</c> only treats the literal value <c>*</c> as a
    /// wildcard ("matches all directories, disabling the check entirely") —
    /// it does not support glob patterns like <c>/mnt/ingest/*</c> for a
    /// prefix. Scoping this to <c>*</c> is acceptable here specifically
    /// because every git invocation in this process goes through this same
    /// wrapper against paths <see cref="ResolveWorkspacePath"/> itself
    /// resolved — nothing in this class ever runs git against an arbitrary
    /// or untrusted directory, so there is no attacker-controlled path for
    /// the disabled check to have protected in the first place, and the
    /// override never outlives the single command it's attached to anyway.
    /// </para>
    /// </remarks>
    private Task RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    ) => git.RunAsync(workingDirectory, [.. SafeDirectoryConfigArgs, .. arguments], cancellationToken);

    /// <summary>Same as <see cref="RunGitAsync"/>, capturing stdout — see its remarks.</summary>
    private Task<string> CaptureGitOutputAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    ) =>
        git.CaptureOutputAsync(
            workingDirectory,
            [.. SafeDirectoryConfigArgs, .. arguments],
            cancellationToken
        );

    /// <summary>
    /// Computes the deterministic workspace path for a job (spec §5.2).
    /// </summary>
    /// <remarks>
    /// Public sources split at the top level from private ones. A private
    /// path is a single opaque hash of org+library+repo URL, not three
    /// plain-text nested segments — see <see cref="PrivateWorkspaceHash"/>.
    /// </remarks>
    private string ResolveWorkspacePath(RepositoryIngestJob job)
    {
        var root = string.IsNullOrWhiteSpace(appSettings.Ingest.ContentRootPath)
            ? Path.GetTempPath()
            : appSettings.Ingest.ContentRootPath;

        if (job.Kind == RepositorySourceKind.Public)
        {
            var repoHash = Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.RepoUrl)))
                .ToLowerInvariant()[..16];

            return Path.Combine(root, "public", repoHash);
        }

        return Path.Combine(root, "private", PrivateWorkspaceHash(job));
    }

    /// <summary>
    /// Collapses org+library+repo URL into one opaque hash instead of three
    /// plain-text nested directory segments.
    /// </summary>
    /// <remarks>
    /// This exists for the mounted-GCS-bucket production deployment (see
    /// <c>docs/content/5.configuration/1-gcp-runtime.md</c>): that bucket is
    /// a single namespace shared across every organization's private clones,
    /// and GCS IAM has no native per-prefix authorization — anyone who can
    /// `list` the bucket (already narrowed to a custom role, but `list`
    /// itself can't be dropped without breaking GCS FUSE's directory
    /// enumeration) previously saw a plain-text organization id as a
    /// top-level folder name, letting them group entries by org — e.g. "org X
    /// has N libraries with active clones" — without reading a single file.
    /// Collapsing all three inputs into one hash removes that grouping
    /// signal: every workspace looks like an unrelated flat leaf, and finding
    /// a specific org's workspace requires already knowing its exact
    /// org+library+repo URL combination to recompute the hash, not just
    /// browsing. Doesn't change any local-dev behavior — the workspace root
    /// is still local temp disk there, where this was never a concern.
    /// </remarks>
    private static string PrivateWorkspaceHash(RepositoryIngestJob job)
    {
        var organizationId =
            job.OrganizationId
            ?? throw new InvalidOperationException("Private ingest jobs require OrganizationId.");
        var libraryId =
            job.LibraryId
            ?? throw new InvalidOperationException("Private ingest jobs require LibraryId.");

        // NUL-separated so e.g. org="ab"+library="c" can never collide with
        // org="a"+library="bc" — plain concatenation could.
        var combined = string.Join('\0', organizationId, libraryId, job.RepoUrl);
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined)))
            .ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Attempts clone-or-pull, falling back to a clean delete-and-reclone on
    /// any failure, per spec §5.3's resilience guidance.
    /// </summary>
    /// <remarks>
    /// The prototype found that repairing a half-written or corrupt clone in
    /// place is not worth the complexity — a fresh clone is cheap under
    /// shallow + partial + sparse, so any failure (corrupt object store,
    /// interrupted prior clone, detached/dirty state, a network blip mid-fetch)
    /// just deletes the workspace and clones clean on the next attempt. The
    /// bound is 2 attempts total; a failure on the second attempt propagates
    /// to the caller (the dispatcher), which surfaces it on the run record
    /// rather than this method swallowing it.
    /// </remarks>
    private async Task CloneOrPullWithRetryAsync(
        string path,
        string repoUrl,
        string? token,
        CancellationToken cancellationToken
    )
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAcquireAttempts; attempt++)
        {
            try
            {
                if (
                    attempt == 1
                    && await IsValidExistingCloneAsync(path, repoUrl, cancellationToken)
                )
                {
                    LogReusingWorkspace(logger, repoUrl, path);
                    await PullAsync(path, token, cancellationToken);
                }
                else
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }

                    Directory.CreateDirectory(path);
                    await CloneAsync(path, repoUrl, token, cancellationToken);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                LogAcquireAttemptFailed(logger, repoUrl, attempt, ex);
                TryDeleteDirectory(path);
            }
        }

        throw new InvalidOperationException(
            $"Failed to acquire ingest workspace for {repoUrl} after {MaxAcquireAttempts} attempts.",
            lastError
        );
    }

    /// <summary>
    /// Checks whether <paramref name="path"/> already holds a clone of
    /// <paramref name="repoUrl"/> that can be pulled forward rather than
    /// re-cloned.
    /// </summary>
    private async Task<bool> IsValidExistingCloneAsync(
        string path,
        string repoUrl,
        CancellationToken cancellationToken
    )
    {
        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            return false;
        }

        try
        {
            var remoteUrl = await CaptureGitOutputAsync(
                path,
                ["remote", "get-url", "origin"],
                cancellationToken
            );

            return string.Equals(remoteUrl, repoUrl, StringComparison.OrdinalIgnoreCase);
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fresh partial, shallow, sparse clone — spec §5.3 items 1–3.
    /// </summary>
    private async Task CloneAsync(
        string path,
        string repoUrl,
        string? token,
        CancellationToken cancellationToken
    )
    {
        await RunGitAsync(
            path,
            [
                .. AuthConfigArgs(token),
                "clone",
                "--filter=blob:none",
                "--depth=1",
                "--no-checkout",
                // Explicit empty template dir: without this, git tries to
                // populate .git/hooks/*.sample from its built-in template
                // directory via a hard link as a fast path (falling back to
                // a plain copy only on failure). On a Cloud Storage FUSE
                // mount, link() isn't supported at all ("function not
                // implemented") — harmless since git treats a failed
                // template copy as non-fatal and the clone still succeeds,
                // but it spams the FUSE driver's own error log with
                // CreateLinkOp failures for files this pipeline never uses
                // (no git hooks are ever invoked here). Skipping template
                // population entirely avoids the hard-link attempts at the
                // source instead of just tolerating the resulting noise.
                "--template=",
                repoUrl,
                ".",
            ],
            cancellationToken
        );

        await RunGitAsync(
            path,
            ["sparse-checkout", "set", "--no-cone", .. MarkdownSparsePatterns],
            cancellationToken
        );

        // Auth is required here too, not just for `clone`: the partial
        // clone above (`--filter=blob:none`) fetched no blobs, so this
        // checkout must lazily fetch the blobs for the sparse-checked-out
        // files from the promisor remote over HTTPS. Without the token, a
        // private repo's checkout fails headless (no credential helper,
        // no TTY) instead of using the same installation token as the clone.
        await RunGitAsync(path, [.. AuthConfigArgs(token), "checkout"], cancellationToken);

        // NOTE: removes any untracked cruft left by a killed prior process
        // (OOM kill, container restart, SIGKILL mid-clone). `reset --hard` and
        // `checkout` only overwrite *tracked* file content — they don't touch
        // untracked files a truncated write could have left behind at this
        // same deterministic path. Cheap and safe: every file this run cares
        // about is tracked, so removing untracked files can never delete
        // anything the ingest pass needs.
        await RunGitAsync(path, ["clean", "-ffdx"], cancellationToken);
    }

    /// <summary>
    /// Pulls an existing clone forward to the remote's current default branch tip.
    /// </summary>
    /// <remarks>
    /// Fetches <c>HEAD</c> explicitly (the remote's default branch) rather than
    /// relying on a locally-tracked <c>origin/HEAD</c> symbolic ref, which a
    /// shallow clone does not reliably keep in sync across fetches. Re-applies
    /// the sparse-checkout pattern set before resetting so a filter change
    /// since the last run is reflected in the working tree.
    /// </remarks>
    private async Task PullAsync(string path, string? token, CancellationToken cancellationToken)
    {
        await RunGitAsync(
            path,
            ["sparse-checkout", "set", "--no-cone", .. MarkdownSparsePatterns],
            cancellationToken
        );

        await RunGitAsync(
            path,
            [
                .. AuthConfigArgs(token),
                "fetch",
                "--depth=1",
                "--filter=blob:none",
                "origin",
                "HEAD",
            ],
            cancellationToken
        );

        await RunGitAsync(path, ["reset", "--hard", "FETCH_HEAD"], cancellationToken);

        // See the matching NOTE in CloneAsync — same untracked-cruft concern
        // applies to a pull that follows a killed prior process.
        await RunGitAsync(path, ["clean", "-ffdx"], cancellationToken);
    }

    /// <summary>
    /// Builds the <c>-c http.extraHeader=...</c> global git option that injects
    /// the token, or an empty argument list for an anonymous public clone.
    /// </summary>
    /// <remarks>
    /// Never an <c>https://x-access-token:&lt;token&gt;@...</c> URL — that form
    /// leaks the secret into the reflog, <c>git remote -v</c>, and process
    /// arguments (spec §5.3). <see cref="GitCommandRunner"/> also redacts this
    /// argument before it reaches a log line or exception message.
    /// <para>
    /// <b>Basic, not Bearer.</b> git's smart-HTTP endpoint (as opposed to the
    /// REST API) only accepts installation tokens via HTTP Basic auth with
    /// <c>x-access-token</c> as the username — confirmed against real GitHub
    /// during Phase 1.8 acceptance testing: a live installation token sent as
    /// <c>Authorization: Bearer &lt;token&gt;</c> got a 401 from
    /// <c>info/refs?service=git-upload-pack</c>, while the same token sent as
    /// <c>Authorization: Basic base64(x-access-token:&lt;token&gt;)</c> got a
    /// 200. All prior unit/integration coverage used fake tokens against a
    /// local git remote, which doesn't validate the auth scheme at all — this
    /// only surfaced against the real github.com.
    /// </para>
    /// </remarks>
    private static string[] AuthConfigArgs(string? token)
    {
        if (token is null)
        {
            return [];
        }

        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}")
        );
        return ["-c", $"http.extraHeader=Authorization: Basic {basic}"];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort cleanup before the next attempt — broadened beyond
            // IOException (e.g. UnauthorizedAccessException on a permission
            // quirk) so a delete failure here can never itself abort the
            // retry loop; a leftover directory just means the next attempt's
            // delete-then-clone does the work instead.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Reusing existing ingest workspace. RepoUrl={RepoUrl}, Path={Path}"
    )]
    private static partial void LogReusingWorkspace(ILogger logger, string repoUrl, string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ingest workspace acquire attempt {Attempt} failed for {RepoUrl} — deleting and retrying."
    )]
    private static partial void LogAcquireAttemptFailed(
        ILogger logger,
        string repoUrl,
        int attempt,
        Exception ex
    );
}
