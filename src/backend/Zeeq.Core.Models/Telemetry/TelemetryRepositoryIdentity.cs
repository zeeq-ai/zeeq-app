using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Core.Models;

/// <summary>
/// Normalizes GitHub remote representations into an owner-qualified repository name.
/// </summary>
public static class TelemetryRepositoryIdentity
{
    /// <summary>
    /// Resolves a harness conversation identifier, using a deterministic repository-and-branch
    /// identity only when the harness does not expose a stable session identifier.
    /// </summary>
    public static string? ResolveConversationId(
        string? harnessSessionId,
        string? repositoryRemoteUrl,
        string? headBranch
    )
    {
        if (!string.IsNullOrWhiteSpace(harnessSessionId))
        {
            return harnessSessionId.Trim();
        }

        var repository = repositoryRemoteUrl is null
            ? string.Empty
            : Normalize(repositoryRemoteUrl);
        if (repository.Length == 0 || string.IsNullOrWhiteSpace(headBranch))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\n{headBranch.Trim()}"));

        return $"branch:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    /// <summary>
    /// Returns a lower-case <c>owner/repository</c> identity when the input is a
    /// GitHub HTTPS, SSH, or already owner-qualified remote; otherwise returns an
    /// empty value that cannot match a configured PR repository.
    /// </summary>
    public static string Normalize(string value)
    {
        var candidate = value.Trim().TrimEnd('/');

        if (candidate.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            candidate = uri.AbsolutePath.Trim('/');
        }

        candidate = candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? candidate[..^4]
            : candidate;

        var segments = candidate.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        return segments.Length == 2 ? string.Join('/', segments).ToLowerInvariant() : string.Empty;
    }
}
