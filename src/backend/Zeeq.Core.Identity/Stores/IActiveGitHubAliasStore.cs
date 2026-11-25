using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>Resolves an active organization member by GitHub alias.</summary>
public interface IActiveGitHubAliasStore
{
    /// <summary>Finds the alias only if its owner is an active member.</summary>
    Task<UserAlias?> FindActiveGitHubAliasAsync(
        string organizationId,
        string normalizedLogin,
        CancellationToken cancellationToken
    );
}
