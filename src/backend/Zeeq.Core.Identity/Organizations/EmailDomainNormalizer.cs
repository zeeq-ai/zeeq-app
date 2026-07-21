using System.Collections.Frozen;

namespace Zeeq.Core.Identity;

/// <summary>
/// Extracts normalized registrable domains from email addresses for same-domain onboarding.
/// </summary>
/// <remarks>
/// This intentionally implements only the email-domain cases needed by onboarding.
/// The curated multi-label suffix list covers common business-email public suffixes
/// without bringing the full Public Suffix List into the hot sign-in path.
/// </remarks>
public static class EmailDomainNormalizer
{
    private static readonly FrozenSet<string> MultiLabelPublicSuffixes = new[]
    {
        "co.uk",
        "gov.uk",
        "org.uk",
        "ac.uk",
        "com.au",
        "net.au",
        "org.au",
        "co.jp",
        "ne.jp",
        "or.jp",
        "com.br",
        "com.mx",
        "com.ar",
        "com.sg",
        "com.tr",
        "co.in",
        "co.nz",
        "co.za",
        "co.kr",
        "com.cn",
        "com.hk",
        "com.tw",
        "github.io",
        "gitlab.io",
        "pages.dev",
        "vercel.app",
        "netlify.app",
        "herokuapp.com",
        "firebaseapp.com",
        "web.app",
        "cloudfront.net",
        "azurewebsites.net",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a normalized registrable domain from an email address, or null
    /// when the value is not usable for same-domain onboarding.
    /// </summary>
    public static string? FromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@') != at)
        {
            return null;
        }

        return FromDomain(trimmed[(at + 1)..]);
    }

    /// <summary>
    /// Returns a normalized registrable domain from a raw domain string, or null
    /// when the domain shape is invalid.
    /// </summary>
    public static string? FromDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var trimmed = domain.Trim();
        if (trimmed.Contains(".."))
        {
            return null;
        }

        var normalized = (
            trimmed.EndsWith(".", StringComparison.Ordinal) ? trimmed[..^1] : trimmed
        ).ToLowerInvariant();
        if (
            normalized.Length is 0 or > 253
            || normalized.StartsWith('.')
            || normalized.Contains(' ')
        )
        {
            return null;
        }

        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2 || labels.Any(IsInvalidLabel))
        {
            return null;
        }

        var suffix = string.Join('.', labels[^2], labels[^1]);
        if (MultiLabelPublicSuffixes.Contains(suffix))
        {
            if (labels.Length < 3)
            {
                return null;
            }

            return string.Join('.', labels[^3], labels[^2], labels[^1]);
        }

        return suffix;
    }

    private static bool IsInvalidLabel(string label)
    {
        if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
        {
            return true;
        }

        return label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-');
    }
}
