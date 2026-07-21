using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Zeeq.Integrations.GitHub.CheckRuns;

/// <summary>
/// Octokit adapter for publishing and updating GitHub check runs.
/// </summary>
/// <remarks>
/// Maps the provider-neutral <see cref="CheckRunWrite"/> to Octokit's
/// <see cref="NewCheckRun"/> and <see cref="CheckRunUpdate"/> payloads.
/// Owner and repository name are parsed from <c>ownerQualifiedRepoName</c>
/// using <c>owner/repo</c> convention.
/// </remarks>
public sealed partial class OctokitCheckRunClient(
    IGitHubClientFactory clientFactory,
    ILogger<OctokitCheckRunClient> logger
) : ICheckRunClient
{
    private const string BypassActionIdentifier = "zeeq_bypass";
    private const string BypassActionLabel = "Bypass";
    private const string BypassActionDescription = "Clear the Zeeq review block";

    /// <inheritdoc />
    public async Task<long> CreateAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        CheckRunWrite write,
        CancellationToken ct
    )
    {
        var (owner, repo) = ParseOwnerRepo(ownerQualifiedRepoName);
        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            organizationId,
            ct
        );
        var payload = ToNewCheckRun(write);

        LogCheckRunCreating(
            logger,
            organizationId,
            ownerQualifiedRepoName,
            write.Name,
            write.HeadSha,
            write.Status.ToString(),
            write.Conclusion?.ToString()
        );

        var result = await client.Check.Run.Create(owner, repo, payload);

        LogCheckRunCreated(
            logger,
            organizationId,
            ownerQualifiedRepoName,
            write.HeadSha,
            result.Id
        );

        return result.Id;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        long checkRunId,
        CheckRunWrite write,
        CancellationToken ct
    )
    {
        var (owner, repo) = ParseOwnerRepo(ownerQualifiedRepoName);
        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            organizationId,
            ct
        );
        var payload = ToCheckRunUpdate(write);

        LogCheckRunUpdating(
            logger,
            organizationId,
            ownerQualifiedRepoName,
            checkRunId,
            write.Status.ToString(),
            write.Conclusion?.ToString()
        );

        await client.Check.Run.Update(owner, repo, checkRunId, payload);

        LogCheckRunUpdated(
            logger,
            organizationId,
            ownerQualifiedRepoName,
            checkRunId
        );
    }

    private static NewCheckRun ToNewCheckRun(CheckRunWrite write)
    {
        var status = write.Status switch
        {
            CheckRunStatusKind.InProgress => new StringEnum<CheckStatus>("in_progress"),
            CheckRunStatusKind.Completed => new StringEnum<CheckStatus>("completed"),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write.Status, "Unhandled CheckRunStatusKind"),
        };

        var output = new NewCheckRunOutput(write.Title, write.Summary);
        var result = new NewCheckRun(write.Name, write.HeadSha)
        {
            Status = status,
            Output = output,
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (write.DetailsUrl is not null)
        {
            result.DetailsUrl = write.DetailsUrl;
        }

        var completedStatus = new StringEnum<CheckStatus>("completed");
        if (status.Equals(completedStatus) && write.Conclusion.HasValue)
        {
            result.Conclusion = ToCheckConclusion(write.Conclusion.Value);
            result.CompletedAt = DateTimeOffset.UtcNow;
        }

        if (write.IncludeBypassAction)
        {
            result.Actions = [ToBypassAction()];
        }

        return result;
    }

    private static CheckRunUpdate ToCheckRunUpdate(CheckRunWrite write)
    {
        var status = write.Status switch
        {
            CheckRunStatusKind.InProgress => new StringEnum<CheckStatus>("in_progress"),
            CheckRunStatusKind.Completed => new StringEnum<CheckStatus>("completed"),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write.Status, "Unhandled CheckRunStatusKind"),
        };

        var output = new NewCheckRunOutput(write.Title, write.Summary);
        var result = new CheckRunUpdate
        {
            Status = status,
            Output = output,
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (write.DetailsUrl is not null)
        {
            result.DetailsUrl = write.DetailsUrl;
        }

        var completedStatus = new StringEnum<CheckStatus>("completed");
        if (status.Equals(completedStatus) && write.Conclusion.HasValue)
        {
            result.Conclusion = ToCheckConclusion(write.Conclusion.Value);
            result.CompletedAt = DateTimeOffset.UtcNow;
        }

        if (write.IncludeBypassAction)
        {
            result.Actions = [ToBypassAction()];
        }

        return result;
    }

    private static StringEnum<CheckConclusion> ToCheckConclusion(
        CheckRunConclusionKind conclusion
    ) =>
        conclusion switch
        {
            CheckRunConclusionKind.Success => new StringEnum<CheckConclusion>("success"),
            CheckRunConclusionKind.Neutral => new StringEnum<CheckConclusion>("neutral"),
            CheckRunConclusionKind.ActionRequired =>
                new StringEnum<CheckConclusion>("action_required"),
            _ => new StringEnum<CheckConclusion>("neutral"),
        };

    private static NewCheckRunAction ToBypassAction() =>
        new(BypassActionLabel, BypassActionDescription, BypassActionIdentifier);

    private static (string Owner, string Repo) ParseOwnerRepo(string ownerQualifiedRepoName)
    {
        var parts = ownerQualifiedRepoName.Split('/');
        if (parts.Length == 2)
        {
            return (parts[0], parts[1]);
        }

        throw new InvalidOperationException(
            $"Expected 'owner/repo' format for ownerQualifiedRepoName, got: '{ownerQualifiedRepoName}'"
        );
    }

    [LoggerMessage(
        EventId = 3310,
        Level = LogLevel.Debug,
        Message = "Creating check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, Name={CheckRunName}, HeadSha={HeadSha}, Status={Status}, Conclusion={Conclusion}"
    )]
    private static partial void LogCheckRunCreating(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        string checkRunName,
        string headSha,
        string status,
        string? conclusion
    );

    [LoggerMessage(
        EventId = 3311,
        Level = LogLevel.Debug,
        Message = "Created check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, HeadSha={HeadSha}, CheckRunId={CheckRunId}"
    )]
    private static partial void LogCheckRunCreated(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        string headSha,
        long checkRunId
    );

    [LoggerMessage(
        EventId = 3312,
        Level = LogLevel.Debug,
        Message = "Updating check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, CheckRunId={CheckRunId}, Status={Status}, Conclusion={Conclusion}"
    )]
    private static partial void LogCheckRunUpdating(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long checkRunId,
        string status,
        string? conclusion
    );

    [LoggerMessage(
        EventId = 3313,
        Level = LogLevel.Debug,
        Message = "Updated check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, CheckRunId={CheckRunId}"
    )]
    private static partial void LogCheckRunUpdated(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long checkRunId
    );
}
