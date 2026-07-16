namespace Zeeq.Core.Carts;

/// <summary>
/// Product-confirmed limits for the findings cart feature.  Used by both server-side
/// validation (defense-in-depth) and client-side enforcement (immediate UX feedback).
/// </summary>
public static class CartLimits
{
    /// <summary>Maximum saved server-side carts an owner may have at once.</summary>
    public const int MaxCartsPerOwner = 5;

    /// <summary>Maximum findings a single cart may contain.</summary>
    public const int MaxItemsPerCart = 10;

    /// <summary>Maximum length of a per-item annotation.</summary>
    public const int MaxAnnotationLength = 500;

    /// <summary>
    /// Regex pattern for client-generated cart IDs. Format: 6-letter adjective,
    /// 4-letter noun, 10-char nanoid (a-zA-Z0-9_-), case-insensitive, hyphen-separated.
    /// Example: snappy-lake-a1b2c3d4e5
    /// </summary>
    public const string CartIdPattern = @"^[a-zA-Z]{6}-[a-zA-Z]{4}-[a-zA-Z0-9_-]{10}$";
}
