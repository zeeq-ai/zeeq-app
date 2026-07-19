using System.Reflection;

namespace Zeeq.Core.Common;

/// <summary>
/// Exposes release version, Git SHA, and build timestamp captured at build time via
/// <c>AssemblyMetadataAttribute</c>, set by the MSBuild target in
/// <c>Directory.Build.targets</c>.
/// </summary>
public static class GitVersionInfo
{
    private static readonly TimeZoneInfo? EstZone = ResolveEstTimeZone();

    /// <summary>
    /// The full Git commit SHA, or <c>null</c> when the build ran outside a git
    /// repository and no <c>GIT_SHA</c> property was provided.
    /// </summary>
    public static string? Sha { get; }

    /// <summary>
    /// The SemVer release version, or <c>null</c> for builds that did not provide
    /// <c>ZEEQ_VERSION</c>.
    /// </summary>
    public static string? Version { get; }

    /// <summary>
    /// The release tag, including the leading <c>v</c>, or <c>null</c> for
    /// builds that did not provide <c>ZEEQ_VERSION_TAG</c>.
    /// </summary>
    public static string? VersionTag { get; }

    /// <summary>
    /// The first 8 characters of <see cref="Sha"/>, or <c>"unknown"</c> when
    /// no commit SHA is available.
    /// </summary>
    public static string ShortSha => Sha?[..Math.Min(Sha.Length, 8)] ?? "unknown";

    /// <summary>
    /// User-facing version text, preferring the SemVer tag when available.
    /// </summary>
    public static string DisplayVersion => VersionTag ?? Version ?? $"dev-{ShortSha}";

    /// <summary>
    /// Version value used by OpenTelemetry resource attributes.
    /// </summary>
    public static string TelemetryVersion => Version ?? Sha ?? "unknown";

    /// <summary>
    /// The UTC build timestamp captured by MSBuild, or <c>null</c> when no
    /// timestamp metadata is present.
    /// </summary>
    public static DateTimeOffset? BuildTimeUtc { get; }

    /// <summary>
    /// The build timestamp converted to <c>America/New_York</c> with an
    /// <c>EST</c> or <c>EDT</c> suffix, or <c>null</c> when unavailable.
    /// </summary>
    public static string? BuildTimeEst
    {
        get
        {
            if (BuildTimeUtc is not { } utc || EstZone is not { } zone)
            {
                return null;
            }

            var est = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, zone);
            var abbrev = zone.IsDaylightSavingTime(est) ? "EDT" : "EST";

            return $"{est:yyyy-MM-dd HH:mm:ss} {abbrev}";
        }
    }

    static GitVersionInfo()
    {
        var assembly = typeof(GitVersionInfo).Assembly;

        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            switch (attr.Key)
            {
                case "GitSha":
                    Sha = attr.Value;

                    break;
                case "BuildTimeUtc":
                    if (DateTimeOffset.TryParse(attr.Value, out var parsed))
                    {
                        BuildTimeUtc = parsed;
                    }

                    break;
                case "Version":
                    Version = string.IsNullOrWhiteSpace(attr.Value) ? null : attr.Value;

                    break;
                case "VersionTag":
                    VersionTag = string.IsNullOrWhiteSpace(attr.Value) ? null : attr.Value;

                    break;
            }
        }
    }

    private static TimeZoneInfo? ResolveEstTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException) { }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException) { }

        return null;
    }
}
