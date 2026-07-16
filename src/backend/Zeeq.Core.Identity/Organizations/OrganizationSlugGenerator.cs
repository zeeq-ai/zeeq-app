using System.Text;

namespace Zeeq.Core.Identity;

/// <summary>
/// Generates URL-safe organization slugs from display names and stable organization IDs.
/// </summary>
public static class OrganizationSlugGenerator
{
    /// <summary>
    /// Creates a slug in the form <c>{display-name-slug}-{last-8-org-id-chars}</c>.
    /// </summary>
    public static string Create(string displayName, string organizationId)
    {
        const int maxSlugLength = 128;
        const int idSuffixLength = 8;

        var idSuffix =
            organizationId.Length > idSuffixLength
                ? organizationId[^idSuffixLength..]
                : organizationId;
        var maxBaseLength = maxSlugLength - idSuffix.Length - 1;
        var slugBase = CreateSlugBase(displayName);

        if (slugBase.Length > maxBaseLength)
        {
            slugBase = slugBase[..maxBaseLength].Trim('-');
        }

        if (slugBase.Length == 0)
        {
            slugBase = "org";
        }

        return $"{slugBase}-{idSuffix}";
    }

    private static string CreateSlugBase(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingHyphen = false;

        foreach (var item in value.ToLowerInvariant())
        {
            if (IsSlugCharacter(item))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(item);
                pendingHyphen = false;
                continue;
            }

            pendingHyphen = builder.Length > 0;
        }

        return builder.ToString();
    }

    private static bool IsSlugCharacter(char item) =>
        item is >= 'a' and <= 'z' or >= '0' and <= '9';
}
