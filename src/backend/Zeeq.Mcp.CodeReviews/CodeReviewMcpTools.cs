using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

/// <summary>
/// MCP tools for uploaded-diff code reviews.
/// </summary>
[McpServerToolType, Description("Provides Zeeq code-review MCP tools.")]
public sealed partial class CodeReviewMcpTools
{
    private static readonly Counter<int> ExpertCodeReviewCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_expert_code_review_total",
            "The total number of times the expert code review MCP tool is called."
        );

    private static readonly Histogram<long> ExpertCodeReviewDurationHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<long>(
            "zeeq_expert_code_review_duration_ms",
            "The elapsed time for expert code review MCP tool calls."
        );

    /// <summary>
    /// Performs an expert code review using an uploaded git diff and context info.
    /// </summary>
    /// <remarks>
    /// The tool is intentionally one endpoint with an action because MCP clients
    /// can keep one workflow in their tool plan: create URL, upload diff, run review.
    /// NOTE: DI will inject the ILoggerFactory for the tool type
    /// </remarks>
    [McpServerTool(Name = "expert_code_review", Title = "Expert Code Review")]
    [Description(
        """
            Request an expert code review by uploading a git diff and providing context about the change.

            <expert_code_review.triggers>
            - Finished coding work execution and need to check for issues using the automated tool before moving to next step
            - Before submitting a PR and want to get feedback before human reviewers berate or excoriate your work
            - After getting feedback from a prior review to check if the changes sufficiently address the feedback or if there are any remaining issues to address
            </expert_code_review.triggers>

            <expert_code_review.flow>
            1. Call with action=`create_upload_url` to get a signed URL + `curl` instructions; no other parameters at this step
            2. Generate a raw unified git diff into a temp file, for example:
               `git diff --binary > /tmp/zeeq-review.diff`
               or `git diff origin/main...HEAD --binary > /tmp/zeeq-review.diff`.
            3. Upload the file to the signed URL using `curl` use `--data-binary`; do not paste or transform the diff.
            4. Call with action=`run_review`, the returned jobId, remoteRepoNameUsingGit, activeOrPlannedBranchName, agentSessionId (if can be resolved from runtime environment), reviewGroupId (from previous result for re-review), and any title/description context.
            </expert_code_review.flow>

            Limits: Max diff size is 500000 bytes

            The upload endpoint expects normal `git diff` / `git show` unified diff output with `diff --git` file headers.
            Produce a diff that includes enough context and relevant files for review since reviewer does not have full codebase.
            Can be a partial diff from the PR or include specific subsets of files for more focused reviews.
            Runtime is usually 20-30 seconds and can take a 60-120 seconds for larger diffs. `run_review` returns XML review output.

            Once `run_review` starts, success, failure, or cancellation consumes the uploaded diff. Use the same signed URL and jobId to upload it again while the URL is valid, then call `run_review` again.
            """
    )]
    public static async Task<string> RunReview(
        IExpertCodeReviewRunner runner,
        IOptions<AppSettings> appSettings,
        ILoggerFactory loggerFactory,
        ClaimsPrincipal? user,
        [Description("Action to perform; exactly one of: `create_upload_url` or `run_review`.")]
            string action,
        [Description("Required for `run_review`; the job ID returned by `create_upload_url`.")]
            string? jobId = null,
        [Description(
            "Required for `run_review`; full upload token returned by `create_upload_url`."
        )]
            string? uploadToken = null,
        [Description(
            "Required for `run_review`; owner/repo remote identifier like `org-name/repo-name` if unknown resolve using `git remote -v`."
        )]
            string? remoteRepoNameUsingGit = null,
        [Description(
            "Optional for `run_review`; a concise summary of the objective of the broader task or feature and purpose of the changes."
        )]
            string? title = null,
        [Description(
            """
                Optional for `run_review`; detailed description of the current state, motivation, objective, and design notes for the PR.
                Always include information about the overall objective, design, and intent of the broader task in this session; this helps the reviewer understand the reasoning.
                What are you building?  What is the feature?  What are the key design decisions that are useful for evaluating the change?  What are key fixes being made?
                For a follow up review or requesting a review after making changes, clearly state the previous findings and the changes you made OR include rationale for deferring or ignoring findings; provide necessary context to the reviewers do not call out explicit decisions.
                """
        )]
            string? description = null,
        [Description(
            "Optional for `run_review`; active git branch, or the planned branch before it exists. Always provide it when known so reviews can be associated with the PR workstream."
        )]
            string? activeOrPlannedBranchName = null,
        [Description(
            "Optional for `run_review`; pass in or pass back a genuine stable agent session ID when the harness exposes one. Do not substitute a branch name here."
        )]
            string? agentSessionId = null,
        [Description(
            "Optional for `run_review`; MUST pass back known review group ID from a previous `expert_code_review` pass to load related reviews for follow up reviews."
        )]
            string? reviewGroupId = null,
        [Description(
            """
                Optional for `run_review`; leave empty to use default libraries configured; use explicit only to override defaults when instructed.
                """
        )]
            string[]? libraries = null,
        CancellationToken cancellationToken = default
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var normalizedAction = action.Trim();
        var result = "error";
        var logger = loggerFactory.CreateLogger<CodeReviewMcpTools>();

        try
        {
            var callResult = normalizedAction switch
            {
                "create_upload_url" => await CreateUploadUrlAsResultAsync(
                    runner,
                    appSettings.Value.CodeReview.DiffUploadMaxBytes,
                    user,
                    cancellationToken
                ),
                "run_review" => await RunUploadedDiffReviewAsResultAsync(
                    runner,
                    jobId,
                    uploadToken,
                    remoteRepoNameUsingGit,
                    title,
                    description,
                    activeOrPlannedBranchName,
                    agentSessionId,
                    reviewGroupId,
                    libraries,
                    user,
                    cancellationToken
                ),
                _ => new ReviewCallResult(
                    $"Unsupported expert_code_review action '{action}'. Use create_upload_url or run_review.",
                    IsError: true
                ),
            };
            result = callResult.IsError ? "error" : "success";

            return callResult.DisplayText;
        }
        catch (Exception ex)
        {
            LogToolCallFailed(logger, normalizedAction, ex);

            return "Expert code review failed: " + ex.Message;
        }
        finally
        {
            stopwatch.Stop();

            var userId = user?.AuthenticatedUser()?.Sub ?? "unknown";

            ExpertCodeReviewCounter.Add(
                1,
                new KeyValuePair<string, object?>("action", normalizedAction),
                new KeyValuePair<string, object?>("result", result),
                new KeyValuePair<string, object?>("user", userId)
            );

            ExpertCodeReviewDurationHistogram.Record(
                stopwatch.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("action", normalizedAction),
                new KeyValuePair<string, object?>("result", result)
            );
        }
    }

    private readonly record struct ReviewCallResult(string DisplayText, bool IsError);

    private static async Task<ReviewCallResult> CreateUploadUrlAsResultAsync(
        IExpertCodeReviewRunner runner,
        int maxDiffSizeBytes,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken
    )
    {
        if (user is null)
        {
            return new ReviewCallResult(
                "An authenticated MCP user is required to create an expert code-review upload URL.",
                IsError: true
            );
        }

        var response = await runner.CreateUploadUrlAsync(user, cancellationToken);

        return new ReviewCallResult(response.RenderAsText(maxDiffSizeBytes), IsError: false);
    }

    private static async Task<ReviewCallResult> RunUploadedDiffReviewAsResultAsync(
        IExpertCodeReviewRunner runner,
        string? jobId,
        string? uploadToken,
        string? ownerQualifiedRepoName,
        string? title,
        string? description,
        string? branch,
        string? agentSessionId,
        string? reviewGroupId,
        string[]? libraries,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken
    ) =>
        await new ExpertCodeReviewRunInput(
            jobId,
            uploadToken,
            ownerQualifiedRepoName,
            title,
            description,
            agentSessionId,
            reviewGroupId,
            branch,
            libraries
        )
            .ToRequest()
            .Match(
                request => RunReviewRequestAsResultAsync(runner, request, user, cancellationToken),
                error => Task.FromResult(new ReviewCallResult(error, IsError: true))
            );

    private static async Task<ReviewCallResult> RunReviewRequestAsResultAsync(
        IExpertCodeReviewRunner runner,
        ExpertCodeReviewRunRequest request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken
    )
    {
        if (user is null)
        {
            return new ReviewCallResult(
                "An authenticated MCP user is required to run an expert code review.",
                IsError: true
            );
        }

        var result = await runner.RunReviewAsync(request, user, cancellationToken);

        return result.Match(
            response => new ReviewCallResult(BuildLocalAgentInstructions(response), IsError: false),
            error => new ReviewCallResult(error, IsError: true)
        );
    }

    private static string BuildLocalAgentInstructions(ExpertCodeReviewRunResponse response) =>
        $$"""
            <instruction_for_agents>
            The following XML is the raw output from expert code reviewers analyzing the PR.
            - Review each finding in the feedback from the expert reviewers
            - Evaluate the validity of each finding in the broader context of the codebase; the reviewer only saw the PR contents and not the broader codebase; determine the veracity of each finding
            - The change proposals are high level; plan out **specific code changes** needed to implement the feedback, finding-by-finding
            - ALWAYS get confirmation and acceptance of the proposed fix for each finding before writing code
            - ALWAYS ensure there is enough clarity to make the best fix if there is ambiguity or insufficient feedback to confidently implement the change
            - Call out any tradeoffs or shortcomings in the proposed changes if any (especially in the broader context of the codebase)
            - Present the concrete changes needed and let the user decide which to proceed with; do not make changes without confirmation
            - Use a checklist/todo list to keep track of work against this set of findings; we do not want to wing it here
            - If a behavior is expected or by design, suggest leaving a comment near the code to explain reasoning to future travelers (agents)
            - Add code comment: "NOTE: (Reason to defer or ignore a finding goes here)" to document any rationale for deferring or ignoring a finding
            </instruction_for_agents>

            <view_in_zeeq>
            A durable record of this review is available in Zeeq at the link: `{{response.ReviewViewUrl}}`
            - reviewId: {{response.ReviewId}}
            - reviewGroupId: {{response.ReviewGroupId}}
            ALWAYS print this link and inform the user that they can open this link to view the full review UI.
            Pass this reviewGroupId, the same activeOrPlannedBranchName, and (when available) the same agentSessionId on the next `expert_code_review` call to chain follow-up reviews.
            </view_in_zeeq>

            {{response.ReviewXml}}
            """;

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Error,
        Message = "Expert code-review MCP tool call failed for action {Action}."
    )]
    private static partial void LogToolCallFailed(
        ILogger logger,
        string action,
        Exception exception
    );
}
