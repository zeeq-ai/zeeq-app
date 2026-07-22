using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using Zeeq.Core.Carts;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Carts;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

public sealed partial class CodeReviewMcpTools
{
    private const string GitHubProvider = "github";

    private static readonly Counter<int> GetReviewFindingsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_get_review_findings_total",
            "The total number of times the get_review_findings MCP tool is called."
        );

    /// <summary>
    /// Retrieves findings for the newest completed review matching a repository and
    /// either a pull request number or branch name.
    /// </summary>
    [McpServerTool(Name = "get_review_findings", Title = "Get Review Findings")]
    [Description(
        """
            Retrieves expert code-review findings for the newest completed review matching a repository
            and either a pull request number or branch name.

            <get_review_findings.triggers>
            - The user asks for review findings for a PR
            - The user asks for review findings for a branch
            - The user asks for review findings above a specific severity level
            - The agent knows the current repository and branch or PR number and needs the latest review findings
            </get_review_findings.triggers>

            Provide ownerQualifiedRepoName in owner/repo form, such as zeeq-ai/zeeq.
            Provide either pullRequestNumber or branch.

            minimumLevel is optional. Supported values are CRITICAL, MAJOR, MINOR, SUGGESTION, and COMMENT.
            Empty means all findings. COMMENT is the lowest level and also means all findings.
            The filter is inclusive: MAJOR returns CRITICAL and MAJOR findings; MINOR returns
            CRITICAL, MAJOR, and MINOR findings.
            """
    )]
    public static async Task<string> GetReviewFindings(
        ICodeRepositoryStore repositories,
        IPullRequestLookupStore pullRequestLookups,
        ICodeReviewRecordStore reviews,
        ICodeReviewArtifactStore artifacts,
        CodeReviewXmlOutputValidator xmlValidator,
        ClaimsPrincipal? user,
        [Description("Required; owner/repo repository identifier, for example zeeq-ai/zeeq.")]
            string ownerQualifiedRepoName,
        [Description(
            "Optional; provider pull request number. Provide either pullRequestNumber or branch."
        )]
            int? pullRequestNumber = null,
        [Description("Optional; source branch name. Provide either branch or pullRequestNumber.")]
            string? branch = null,
        [Description(
            "Optional; minimum finding level: CRITICAL, MAJOR, MINOR, SUGGESTION, COMMENT. Empty means all."
        )]
            string? minimumLevel = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(ownerQualifiedRepoName))
        {
            return RecordGetReviewFindingsCall(
                "missing_repository",
                "ownerQualifiedRepoName is required. Tip: git remote get-url origin"
            );
        }

        if (pullRequestNumber is not null && !string.IsNullOrWhiteSpace(branch))
        {
            return RecordGetReviewFindingsCall(
                "ambiguous_target",
                "Provide either pullRequestNumber or branch, not both."
            );
        }

        if (pullRequestNumber is null && string.IsNullOrWhiteSpace(branch))
        {
            return RecordGetReviewFindingsCall(
                "missing_target",
                "Provide either pullRequestNumber or branch. Tip: git branch --show-current"
            );
        }

        if (pullRequestNumber is <= 0 or > 999_999)
        {
            return RecordGetReviewFindingsCall(
                "invalid_pull_request_number",
                "pullRequestNumber must be between 1 and 999999. Tip: gh pr view --json number"
            );
        }

        if (!TryParseMinimumLevel(minimumLevel, out var parsedMinimumLevel))
        {
            return RecordGetReviewFindingsCall(
                "invalid_minimum_level",
                "minimumLevel must be one of CRITICAL, MAJOR, MINOR, SUGGESTION, or COMMENT."
            );
        }

        var organizationId = user?.AsZeeqMinimalIdentity().OrganizationId;

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return RecordGetReviewFindingsCall("missing_org", "Active organization is required.");
        }

        var repository = await repositories.FindActiveForOrganizationByProviderIdentityAsync(
            organizationId,
            GitHubProvider,
            ownerQualifiedRepoName.Trim(),
            cancellationToken
        );

        if (repository is null)
        {
            return RecordGetReviewFindingsCall(
                "repository_not_found",
                $"Repository '{ownerQualifiedRepoName}' was not found in the active organization. Tip: git remote get-url origin"
            );
        }

        var review = pullRequestNumber is { } number
            ? await FindNewestCompletedForPullRequestAsync(
                pullRequestLookups,
                reviews,
                organizationId,
                repository.Id,
                number,
                cancellationToken
            )
            : await reviews.FindNewestCompletedForBranchAsync(
                organizationId,
                repository.Id,
                branch!.Trim(),
                cancellationToken
            );

        if (review is null)
        {
            return RecordGetReviewFindingsCall(
                "review_not_found",
                "No completed review was found for the requested repository target. Tip: git branch --show-current"
            );
        }

        if (string.IsNullOrWhiteSpace(review.FindingsStorageUri))
        {
            if (TotalFindings(review) == 0)
            {
                return RecordGetReviewFindingsCall(
                    "success",
                    Array.Empty<CartFindingSnapshot>().ToAgentInstructions()
                );
            }

            return RecordGetReviewFindingsCall(
                "missing_findings_artifact",
                "The newest completed review has findings but no stored findings artifact."
            );
        }

        var findingsXml = await ReadFindingsXmlAsync(
            artifacts,
            review.FindingsStorageUri,
            cancellationToken
        );
        var validation = xmlValidator.Validate(findingsXml);

        if (!validation.IsValid || validation.Output is null)
        {
            return RecordGetReviewFindingsCall(
                "invalid_findings_artifact",
                validation.ErrorMessage ?? "Code review findings artifact could not be parsed."
            );
        }

        var snapshots = validation
            .Output.Reviews.SelectMany(reviewer =>
                reviewer
                    .Findings.Where(finding => MeetsMinimumLevel(finding.Level, parsedMinimumLevel))
                    .Select(finding => ToCartFindingSnapshot(review, reviewer, finding))
            )
            .ToArray();

        return RecordGetReviewFindingsCall("success", snapshots.ToAgentInstructions());
    }

    private static async Task<CodeReviewRecord?> FindNewestCompletedForPullRequestAsync(
        IPullRequestLookupStore pullRequestLookups,
        ICodeReviewRecordStore reviews,
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        var lookup = await pullRequestLookups.FindAsync(
            organizationId,
            repositoryId,
            pullRequestNumber,
            cancellationToken
        );

        return lookup is null
            ? null
            : await reviews.FindNewestCompletedForPullRequestAsync(
                organizationId,
                lookup.PullRequestRecordId,
                lookup.PullRequestCreatedAtUtc,
                cancellationToken
            );
    }

    private static async Task<string> ReadFindingsXmlAsync(
        ICodeReviewArtifactStore artifacts,
        string findingsStorageUri,
        CancellationToken cancellationToken
    )
    {
        await using var stream = await artifacts.OpenFindingsAsync(
            findingsStorageUri,
            cancellationToken
        );
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static CartFindingSnapshot ToCartFindingSnapshot(
        CodeReviewRecord review,
        CodeReviewFacetOutput reviewer,
        CodeReviewFindingOutput finding
    ) =>
        new(
            BuildFindingHash(review, reviewer, finding),
            finding.Summary,
            ToCartCriticality(finding.Level),
            finding.File,
            finding.Line > 0 ? finding.Line : null,
            string.IsNullOrWhiteSpace(finding.Side) ? null : finding.Side,
            finding.Summary,
            finding.Details,
            review.OwnerQualifiedRepoName,
            review.PullRequestNumber,
            reviewer.Facet,
            reviewer.Agent,
            Annotation: null,
            review.CreatedAtUtc
        );

    private static bool TryParseMinimumLevel(
        string? value,
        out CodeReviewFindingLevel? minimumLevel
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            minimumLevel = null;
            return true;
        }

        var trimmed = value.Trim();

        minimumLevel = trimmed.ToUpperInvariant() switch
        {
            "CRITICAL" => CodeReviewFindingLevel.Critical,
            "MAJOR" => CodeReviewFindingLevel.Major,
            "MINOR" => CodeReviewFindingLevel.Minor,
            "SUGGESTION" => CodeReviewFindingLevel.Suggestion,
            "COMMENT" => CodeReviewFindingLevel.Comment,
            _ => null,
        };

        return minimumLevel is not null;
    }

    private static bool MeetsMinimumLevel(
        CodeReviewFindingLevel level,
        CodeReviewFindingLevel? minimumLevel
    ) => minimumLevel is null || FindingLevelRank(level) <= FindingLevelRank(minimumLevel.Value);

    private static int FindingLevelRank(CodeReviewFindingLevel level) =>
        level switch
        {
            CodeReviewFindingLevel.Critical => 0,
            CodeReviewFindingLevel.Major => 1,
            CodeReviewFindingLevel.Minor => 2,
            CodeReviewFindingLevel.Suggestion => 3,
            CodeReviewFindingLevel.Comment => 4,
            _ => int.MaxValue,
        };

    private static string ToCartCriticality(CodeReviewFindingLevel level) =>
        level switch
        {
            CodeReviewFindingLevel.Critical => "Critical",
            CodeReviewFindingLevel.Major => "Major",
            CodeReviewFindingLevel.Minor => "Minor",
            CodeReviewFindingLevel.Suggestion => "Suggestion",
            CodeReviewFindingLevel.Comment => "Comment",
            _ => level.ToString(),
        };

    private static string BuildFindingHash(
        CodeReviewRecord review,
        CodeReviewFacetOutput reviewer,
        CodeReviewFindingOutput finding
    )
    {
        var stableText = string.Join(
            '\u001f',
            review.Id,
            review.CreatedAtUtc.ToUnixTimeMilliseconds().ToString("D"),
            reviewer.Facet,
            reviewer.Agent,
            finding.Level.ToString(),
            finding.File,
            finding.Line.ToString("D"),
            finding.Side ?? string.Empty,
            finding.Summary,
            finding.Details
        );

        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableText)))
            .ToLowerInvariant();
    }

    private static int TotalFindings(CodeReviewRecord review) =>
        review.CriticalFindings
        + review.MajorFindings
        + review.MinorFindings
        + review.SuggestionFindings
        + review.CommentFindings;

    private static string RecordGetReviewFindingsCall(string result, string response)
    {
        GetReviewFindingsCounter.Add(1, new KeyValuePair<string, object?>("result", result));

        return response;
    }
}
