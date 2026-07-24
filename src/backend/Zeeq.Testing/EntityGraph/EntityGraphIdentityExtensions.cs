using Zeeq.Core.Models;

namespace Zeeq.Testing.EntityGraphs;

/// <summary>
/// Test-builder prototype for organization-scoped user aliases.
/// </summary>
public sealed class UserAliasPrototype
{
    /// <summary>
    /// User that owns the alias. Defaults to the seed owner.
    /// </summary>
    public User? User { get; set; }

    /// <summary>
    /// Organization that scopes the alias. Defaults to the seed organization.
    /// </summary>
    public Organization? Organization { get; set; }

    /// <summary>
    /// Alias namespace.
    /// </summary>
    public UserAliasKind Kind { get; set; } = UserAliasKind.Email;

    /// <summary>
    /// User-facing alias value. A generated value is used when omitted.
    /// </summary>
    public string? DisplayValue { get; set; }

    /// <summary>
    /// Canonical lookup value. Defaults to a simple lower-case normalization of
    /// <see cref="DisplayValue" />.
    /// </summary>
    public string? NormalizedValue { get; set; }

    /// <summary>
    /// Timestamp when this alias was verified, if any.
    /// </summary>
    public DateTimeOffset? VerifiedAtUtc { get; set; }

    /// <summary>
    /// Created timestamp for the generated alias row.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Disabled timestamp for the generated alias row.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }

    /// <summary>
    /// Whether the generated alias should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Identity-specific entity graph helpers.
/// </summary>
public static class EntityGraphIdentityExtensions
{
    extension<TState>(EntityGraphBuilder<TState> builder)
    {
        /// <summary>
        /// Adds the requested number of default organization-scoped user aliases.
        /// </summary>
        /// <param name="count">Number of aliases to create.</param>
        /// <returns>A builder with the aliases in the result tuple.</returns>
        public EntityGraphBuilder<(TState Previous, UserAlias[] Aliases)> AddUserAliases(
            int count
        ) =>
            builder.AddUserAliases([
                .. Enumerable.Range(0, count).Select(_ => (Action<UserAliasPrototype>)(_ => { })),
            ]);

        /// <summary>
        /// Adds one organization-scoped user alias per prototype customization action.
        /// </summary>
        /// <param name="customize">
        /// Function supplied at the call site to customize each alias prototype.
        /// Empty creates one default alias.
        /// </param>
        /// <returns>A builder with the aliases in the result tuple.</returns>
        public EntityGraphBuilder<(TState Previous, UserAlias[] Aliases)> AddUserAliases(
            params Action<UserAliasPrototype>[] customize
        )
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var aliases = new UserAlias[customize.Length];
            var nonPersistentAliases = new List<object>();

            for (var index = 0; index < aliases.Length; index++)
            {
                var prototype = new UserAliasPrototype();
                customize[index].Invoke(prototype);

                var alias = CreateUserAlias(builder.Seed, prototype, index);
                aliases[index] = alias;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentAliases.Add(alias);
                }
            }

            return builder.Push(aliases, nonPersistentAliases);
        }
    }

    private static UserAlias CreateUserAlias(
        SeedContext seed,
        UserAliasPrototype prototype,
        int index
    )
    {
        var now = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var displayValue =
            prototype.DisplayValue
            ?? (
                prototype.Kind == UserAliasKind.GitHub
                    ? $"github-alias-{index + 1}"
                    : $"alias-{index + 1}@example.test"
            );

        return new()
        {
            Id = SeedContext.NewId("alias"),
            OrganizationId = (prototype.Organization ?? seed.Organization).Id,
            UserId = (prototype.User ?? seed.Owner).Id,
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
