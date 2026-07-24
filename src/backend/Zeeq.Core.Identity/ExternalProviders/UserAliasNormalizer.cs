using System.Net.Mail;
using System.Text.RegularExpressions;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Normalizes user-entered aliases into stable lookup values.
/// </summary>
internal static partial class UserAliasNormalizer
{
    public const int MaxAliasesPerKind = 3;
    public const int MaxAliasLength = 320;

    /// <summary>
    /// Converts UI alias arrays into distinct validated write models.
    /// </summary>
    public static IReadOnlyList<UserAliasWrite> ToWrites(
        IReadOnlyList<string?>? emailAliases,
        IReadOnlyList<string?>? gitHubAliases
    )
    {
        if (emailAliases?.Count > MaxAliasesPerKind)
        {
            throw new ArgumentException(
                $"Email aliases are limited to {MaxAliasesPerKind} entries."
            );
        }

        if (gitHubAliases?.Count > MaxAliasesPerKind)
        {
            throw new ArgumentException(
                $"GitHub aliases are limited to {MaxAliasesPerKind} entries."
            );
        }

        var writes = new List<UserAliasWrite>();
        var seen = new HashSet<(UserAliasKind Kind, string NormalizedValue)>();

        foreach (var alias in emailAliases ?? [])
        {
            var write = NormalizeEmail(alias);
            if (seen.Add((write.Kind, write.NormalizedValue)))
            {
                writes.Add(write);
            }
        }

        foreach (var alias in gitHubAliases ?? [])
        {
            var write = NormalizeGitHub(alias);
            if (seen.Add((write.Kind, write.NormalizedValue)))
            {
                writes.Add(write);
            }
        }

        return writes;
    }

    private static UserAliasWrite NormalizeEmail(string? value)
    {
        if (value is null)
        {
            throw new ArgumentException("Email aliases cannot be null.");
        }

        var display = value.Trim();
        if (display.Length == 0)
        {
            throw new ArgumentException("Email aliases cannot be blank.");
        }

        if (display.Length > MaxAliasLength)
        {
            throw new ArgumentException(
                $"Email aliases cannot exceed {MaxAliasLength} characters."
            );
        }

        try
        {
            var address = new MailAddress(display);
            if (!string.Equals(address.Address, display, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Email alias must be a single email address.");
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException($"'{display}' is not a valid email alias.");
        }

        var normalized = NormalizeEmailValue(display);
        if (normalized.Length > MaxAliasLength)
        {
            throw new ArgumentException(
                $"Email aliases cannot exceed {MaxAliasLength} characters."
            );
        }

        return new(UserAliasKind.Email, display, normalized);
    }

    private static UserAliasWrite NormalizeGitHub(string? value)
    {
        if (value is null)
        {
            throw new ArgumentException("GitHub aliases cannot be null.");
        }

        var display = value.Trim().TrimStart('@');
        if (display.Length == 0)
        {
            throw new ArgumentException("GitHub aliases cannot be blank.");
        }

        if (!GitHubLoginRegex().IsMatch(display))
        {
            throw new ArgumentException($"'{display}' is not a valid GitHub alias.");
        }

        return new(UserAliasKind.GitHub, display, NormalizeGitHubValue(display));
    }

    public static string NormalizeEmailValue(string value) => value.Trim().ToLowerInvariant();

    public static string NormalizeGitHubValue(string value) =>
        value.Trim().TrimStart('@').ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex GitHubLoginRegex();
}
