using Zeeq.Biome;
using Zeeq.Core.Models;
using Zeeq.Testing.Biomes;

namespace Zeeq.Testing.Biomes;

/// <summary>Aliases belong to explicitly anchored users.</summary>
[BiomeGraft]
public static class IdentityBlueprints
{
    /// <summary>Alias child with normalization from the final display value.</summary>
    [Graft("Alias", Plural = "Aliases")]
    public static readonly ChildInputBlueprint<SeedContext, User, UserAliasInput, UserAlias> Alias =
        Blueprint.ChildFromInput<SeedContext, User, UserAliasInput, UserAlias>(
            () => new(),
            (parent, input, ctx) => CreateUserAlias(ctx.Seed, input, parent, ctx)
        );

    private static UserAlias CreateUserAlias(
        SeedContext seed,
        UserAliasInput prototype,
        User parent,
        BiomeContext<SeedContext> ctx
    )
    {
        var now = prototype.CreatedAtUtc ?? ctx.Now;
        var displayValue =
            prototype.DisplayValue
            ?? (
                prototype.Kind == UserAliasKind.GitHub
                    ? $"github-alias-{ctx.Index + 1}"
                    : $"alias-{ctx.Index + 1}@example.test"
            );

        return new()
        {
            Id = SeedContext.NewId("alias"),
            OrganizationId = (prototype.Organization ?? seed.Organization).Id,
            UserId = parent.Id,
            Kind = prototype.Kind,
            DisplayValue = displayValue,
            NormalizedValue = prototype.NormalizedValue ?? NormalizeAliasValue(displayValue),
            VerifiedAtUtc = prototype.VerifiedAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            DisabledAtUtc = prototype.DisabledAtUtc,
        };
    }

    private static string NormalizeAliasValue(string value) =>
        value.Trim().TrimStart('@').ToLowerInvariant();
}
