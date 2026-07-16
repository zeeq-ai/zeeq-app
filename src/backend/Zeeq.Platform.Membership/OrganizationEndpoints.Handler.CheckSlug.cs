using System.Text.RegularExpressions;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Validates slug format and checks availability against the
/// organization table. Used by the org settings UI for inline validation.
/// </summary>
public sealed partial class CheckSlugHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Validates slug format and checks database availability.
    /// Supports an <paramref name="excludeOrgId"/> so an org can keep
    /// its own slug on edit.
    /// </summary>
    public async Task<Results<Ok<SlugCheckResponse>, BadRequest>> HandleAsync(
        string slug,
        string? excludeOrgId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 128)
            return TypedResults.BadRequest();

        if (!SlugRegex().IsMatch(slug))
            return TypedResults.BadRequest();

        var available = await store.IsSlugAvailableAsync(slug, excludeOrgId, ct);

        return TypedResults.Ok(new SlugCheckResponse(slug, available));
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();
}
