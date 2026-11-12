using System.Text.RegularExpressions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves and captures pull request targets for dataset curation.
/// </summary>
/// <remarks>
/// Manual entries accept bare numbers, <c>#</c> numbers, comma or newline separated lists and
/// same-repository GitHub pull request URLs. Resolution is per entry: a bad entry never clears
/// the valid choices in the same request. Every valid entry is captured immediately and
/// returns a signed token, because new dataset membership cannot be saved without one.
/// </remarks>
public sealed partial class CodeReviewEvaluationPrTargetService(
    ICodeReviewEvaluationPrTargetStore targets,
    CodeReviewEvaluationCaptureService captures
)
{
    /// <summary>Browses known pull requests without staging anything.</summary>
    public async Task<CodeReviewEvaluationPrTargetPage> BrowseAsync(
        CodeReviewEvaluationPrTargetQuery query,
        IReadOnlySet<int> alreadyInDataset,
        CancellationToken cancellationToken
    )
    {
        var page = await targets.SearchAsync(query, cancellationToken);
        return page;
    }

    /// <summary>Resolves and captures each entry independently.</summary>
    public async Task<IReadOnlyList<CodeReviewEvaluationPrResolution>> ResolveAsync(
        string organizationId,
        string repositoryId,
        string ownerQualifiedRepoName,
        string datasetId,
        long expectedDatasetVersion,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken
    )
    {
        var slots = ParseEntries(entries, ownerQualifiedRepoName);
        var results = new List<CodeReviewEvaluationPrResolution>(slots.Count);

        // Results preserve the caller's fragment order, mixing successful resolutions and
        // per-entry errors exactly where the entry appeared.
        foreach (var slot in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (slot.Parsed is null)
            {
                results.Add(
                    new(slot.Entry, null, null, null, slot.ErrorCode, slot.ErrorMessage)
                );
                continue;
            }

            var entry = slot.Parsed;

            try
            {
                var known = await targets.FindAsync(
                    organizationId,
                    repositoryId,
                    entry.PullRequestNumber,
                    cancellationToken
                );

                var capture = await captures.CaptureAsync(
                    organizationId,
                    repositoryId,
                    ownerQualifiedRepoName,
                    datasetId,
                    expectedDatasetVersion,
                    entry.PullRequestNumber,
                    cancellationToken
                );

                results.Add(new(entry.Entry, entry.PullRequestNumber, known, capture, null, null));
            }
            catch (CodeReviewEvaluationCaptureException exception)
            {
                results.Add(
                    new(
                        entry.Entry,
                        entry.PullRequestNumber,
                        null,
                        null,
                        exception.Code,
                        exception.Message
                    )
                );
            }
        }

        return results;
    }

    private static List<EntrySlot> ParseEntries(
        IReadOnlyList<string> entries,
        string ownerQualifiedRepoName
    )
    {
        var slots = new List<EntrySlot>();
        var seenNumbers = new HashSet<int>();
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddParsed(string entry, int number)
        {
            // The same pull request is resolved once, at its first occurrence.
            if (number <= 0 || !seenNumbers.Add(number))
            {
                return;
            }

            slots.Add(new(entry, new ParsedEntry(entry, number), null, null));
        }

        void AddInvalid(string entry, string code, string message) =>
            slots.Add(new(entry, null, code, message));

        foreach (var rawEntry in entries)
        {
            foreach (
                var fragment in rawEntry.Split(
                    [',', '\n', '\r'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
            {
                if (!seenEntries.Add(fragment))
                {
                    continue;
                }

                var match = GitHubPullRequestUrlPattern().Match(fragment);
                if (match.Success)
                {
                    var owner = match.Groups["owner"].Value;
                    var repository = match.Groups["repo"].Value;

                    if (
                        !string.Equals(
                            $"{owner}/{repository}",
                            ownerQualifiedRepoName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        AddInvalid(
                            fragment,
                            "pr_cross_repository",
                            "Only pull requests from this repository can be added."
                        );
                        continue;
                    }

                    // A hostile or malformed URL can carry a number far beyond int range.
                    // Overflow must be a per-entry validation error, never a batch failure.
                    if (TryParseNumber(match.Groups["number"].Value, out var urlNumber))
                    {
                        AddParsed(fragment, urlNumber);
                    }
                    else
                    {
                        AddInvalid(
                            fragment,
                            "pr_invalid_entry",
                            "Enter a pull request number or a link to a pull request in this repository."
                        );
                    }

                    continue;
                }

                if (TryParseNumber(fragment, out var number))
                {
                    AddParsed(fragment, number);
                    continue;
                }

                AddInvalid(
                    fragment,
                    "pr_invalid_entry",
                    "Enter a pull request number or a link to a pull request in this repository."
                );
            }
        }

        return slots;
    }

    private static bool TryParseNumber(string value, out int number)
    {
        number = 0;
        var trimmed = value.Trim().TrimStart('#').Trim();

        return int.TryParse(trimmed, out number) && number is > 0 and <= 999_999;
    }

    [GeneratedRegex(
        @"^https?://(?:www\.)?github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/pull/(?<number>\d+)/?(?:[?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex GitHubPullRequestUrlPattern();

    private sealed record ParsedEntry(string Entry, int PullRequestNumber);

    private sealed record EntrySlot(
        string Entry,
        ParsedEntry? Parsed,
        string? ErrorCode,
        string? ErrorMessage
    );
}

/// <summary>
/// One per-entry resolution outcome.
/// </summary>
/// <param name="Entry">Original entry text.</param>
/// <param name="PullRequestNumber">Resolved provider pull request number.</param>
/// <param name="Target">Known pull request row, when the pull request already exists in Zeeq.</param>
/// <param name="Capture">Staged capture with its token, when capture succeeded.</param>
/// <param name="ErrorCode">Stable per-entry error code.</param>
/// <param name="ErrorMessage">Human-readable per-entry error.</param>
public sealed record CodeReviewEvaluationPrResolution(
    string Entry,
    int? PullRequestNumber,
    CodeReviewEvaluationPrTargetRecord? Target,
    CodeReviewEvaluationCaptureOutcome? Capture,
    string? ErrorCode,
    string? ErrorMessage
);
