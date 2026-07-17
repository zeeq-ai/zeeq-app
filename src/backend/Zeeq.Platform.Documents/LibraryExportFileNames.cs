using System.Text;
using System.Text.RegularExpressions;
using NanoidDotNet;

namespace Zeeq.Platform.Documents;

internal static partial class LibraryExportFileNames
{
    private const int DownloadIdLength = 6;

    public static string Create(string libraryName, DateOnly date, string extension)
    {
        var slug = Slugify(libraryName);
        var downloadId = Nanoid.Generate(
            Nanoid.Alphabets.LowercaseLettersAndDigits,
            DownloadIdLength
        );
        return $"{slug}-{date:yyyy-MM-dd}-{downloadId}.{extension}";
    }

    internal static string Slugify(string libraryName)
    {
        var lower = libraryName.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        var previousWasSeparator = false;

        foreach (var ch in lower)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var slug = TrimHyphens().Replace(builder.ToString(), string.Empty);
        return string.IsNullOrWhiteSpace(slug) ? "library" : slug;
    }

    [GeneratedRegex("^-+|-+$")]
    private static partial Regex TrimHyphens();
}
