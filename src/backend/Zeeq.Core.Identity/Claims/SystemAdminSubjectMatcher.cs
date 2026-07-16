namespace Zeeq.Core.Identity;

/// <summary>
/// Matches an authenticated upstream identity against configured system-admin subjects.
/// </summary>
/// <remarks>
/// The only supported admin key is `provider:subject`. The provider portion is a
/// stable local provider key and is compared case-insensitively; the subject is
/// an opaque IdP identifier and remains case-sensitive. Email is intentionally
/// absent from this API because it is not a stable authorization key.
/// </remarks>
public static class SystemAdminSubjectMatcher
{
    /// <summary>
    /// Returns whether the provider and subject match a configured system-admin entry.
    /// </summary>
    /// <param name="provider">Stable local provider key from <see cref="AuthClaims.Provider"/>.</param>
    /// <param name="subject">
    /// Verified upstream subject from <see cref="AuthClaims.ProviderSubject"/>.
    /// </param>
    /// <param name="configuredSubjects">
    /// Configured allow-list entries in `provider:subject` format.
    /// </param>
    public static bool IsSystemAdminSubject(
        string? provider,
        string? subject,
        IEnumerable<string?> configuredSubjects
    )
    {
        var incoming = ParseSubjectKey(ComposeSubjectKey(provider, subject));
        if (incoming is null)
        {
            return false;
        }

        foreach (var configuredSubject in configuredSubjects)
        {
            var configured = ParseSubjectKey(configuredSubject);
            if (configured is null)
            {
                continue;
            }

            if (
                StringComparer.OrdinalIgnoreCase.Equals(
                    configured.Value.Provider,
                    incoming.Value.Provider
                ) && StringComparer.Ordinal.Equals(configured.Value.Subject, incoming.Value.Subject)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string? ComposeSubjectKey(string? provider, string? subject)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return $"{provider}:{subject}".Trim();
    }

    private static (string Provider, string Subject)? ParseSubjectKey(string? subjectKey)
    {
        var trimmed = subjectKey?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var separatorIndex = trimmed.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == trimmed.Length - 1)
        {
            return null;
        }

        var provider = trimmed[..separatorIndex].Trim();
        var subject = trimmed[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return (provider, subject);
    }
}
