import { describe, expect, it } from "vitest";
import {
  codeReviewFindingLevelEnum,
  codeReviewRequestOriginEnum,
  codeReviewStatusEnum,
  codeReviewAgentTestRunResultKindEnum,
  pullRequestClaimStatusEnum,
  pullRequestStateEnum,
  type CodeReviewAgentTestRunResponse,
  type CodeReviewPullRequestDto,
} from "@/api/generated";

import {
  agentTestLocationLabel,
  buildAgentTestSeverityTabs,
  buildAgentTestSummaryMetrics,
  buildAgentTestTargetRows,
  buildReviewerSectionsByLevel,
  totalAgentTestFindingCount,
} from "./agent-test-view-models";

describe("agent test view models", () => {
  it("projects pull requests into compact listbox rows", () => {
    const rows = buildAgentTestTargetRows([
      pullRequest({
        id: "pr_1",
        pullRequestNumber: 1,
        title: "Draft target",
        isDraft: true,
        state: pullRequestStateEnum.Open,
      }),
      pullRequest({
        id: "pr_2",
        pullRequestNumber: 2,
        title: "Merged target",
        isDraft: false,
        state: pullRequestStateEnum.Merged,
      }),
    ]);

    expect(rows[0]).toMatchObject({
      value: "pr_1:2026-07-27T12:00:00.000Z",
      label: "#1 Draft target",
      repo: "zeeq-ai/zeeq-test",
      authorLogin: "octocat",
      isDraft: true,
      stateColor: "info",
      iconClass: "text-warning",
    });
    expect(rows[1].stateColor).toBe("success");
  });

  it("groups returned findings by severity and counts totals", () => {
    const sections = buildReviewerSectionsByLevel(
      testResult({
        findings: {
          reviews: [
            {
              facet: "Correctness",
              agent: "Draft reviewer",
              summary: "Summary",
              details: "Details",
              findings: [
                {
                  level: codeReviewFindingLevelEnum.Major,
                  file: "src/App.cs",
                  line: 10,
                  side: "RIGHT",
                  summary: "Major issue",
                  body: "Fix this.",
                },
                {
                  level: codeReviewFindingLevelEnum.Comment,
                  file: "src/App.cs",
                  line: null,
                  side: null,
                  summary: "Context",
                  body: "Note this.",
                },
              ],
            },
          ],
        },
      }),
    );

    const tabs = buildAgentTestSeverityTabs(sections);

    expect(sections[codeReviewFindingLevelEnum.Major]).toHaveLength(1);
    expect(sections[codeReviewFindingLevelEnum.Comment]).toHaveLength(1);
    expect(tabs.find((tab) => tab.value === "major")?.count).toBe(1);
    expect(tabs.find((tab) => tab.value === "major")?.disabled).toBe(false);
    expect(tabs.find((tab) => tab.value === "comments")?.count).toBe(1);
    expect(tabs.find((tab) => tab.value === "critical")?.disabled).toBe(true);
    expect(totalAgentTestFindingCount(tabs)).toBe(2);
  });

  it("formats summary fields and finding locations", () => {
    const result = testResult({
      inScopeFileCount: "3",
      outOfScopeFileCount: 2,
      reviewerCount: "1",
    });

    expect(buildAgentTestSummaryMetrics(result)).toEqual([
      { label: "Files in scope", value: "3" },
      { label: "Files filtered out", value: "2" },
      { label: "Run time", value: "1m" },
    ]);
    expect(
      agentTestLocationLabel({
        level: codeReviewFindingLevelEnum.Minor,
        file: "src/App.cs",
        line: 12,
        side: "RIGHT",
        summary: "Minor issue",
        body: "Body",
      }),
    ).toBe("src/App.cs:12 (RIGHT)");
  });
});

function pullRequest(
  overrides: Partial<CodeReviewPullRequestDto> = {},
): CodeReviewPullRequestDto {
  return {
    id: "pr_123",
    createdAtUtc: new Date("2026-07-27T12:00:00.000Z"),
    updatedAtUtc: new Date("2026-07-27T12:05:00.000Z"),
    organizationId: "org_123",
    teamId: null,
    repositoryId: "repo_123",
    ownerQualifiedRepoName: "zeeq-ai/zeeq-test",
    pullRequestNumber: 1,
    title: "Test PR",
    branch: "feature/test",
    baseBranch: "main",
    headSha: "abc123",
    authorLogin: "octocat",
    htmlUrl: "https://github.test/zeeq-ai/zeeq-test/pull/1",
    isDraft: false,
    state: pullRequestStateEnum.Open,
    claimStatus: pullRequestClaimStatusEnum.Unclaimed,
    claimedByUserId: null,
    featureId: null,
    lastWebhookAtUtc: new Date("2026-07-27T12:05:00.000Z"),
    singleViewToken: "token",
    checkRunBlocking: false,
    ...overrides,
  };
}

type TestResultOverrides = Omit<
  Partial<CodeReviewAgentTestRunResponse>,
  "findings"
> & {
  findings?: Partial<CodeReviewAgentTestRunResponse["findings"]>;
};

function testResult(
  overrides: TestResultOverrides = {},
): CodeReviewAgentTestRunResponse {
  const { findings: findingsOverride, ...responseOverrides } = overrides;

  return {
    resultKind: codeReviewAgentTestRunResultKindEnum.Completed,
    pullRequest: pullRequest(),
    review: {
      id: "synthetic_123",
      createdAtUtc: new Date("2026-07-27T12:10:00.000Z"),
      updatedAtUtc: new Date("2026-07-27T12:11:00.000Z"),
      pullRequestRecordId: "pr_123",
      repositoryId: "repo_123",
      ownerQualifiedRepoName: "zeeq-ai/zeeq-test",
      pullRequestNumber: 1,
      branch: "feature/test",
      title: "Test PR",
      authorLogin: "octocat",
      status: codeReviewStatusEnum.Completed,
      requestOrigin: codeReviewRequestOriginEnum.Manual,
      reviewGroupId: null,
      remainingReviewBudget: 0,
      criticalFindings: 0,
      majorFindings: 0,
      minorFindings: 0,
      suggestionFindings: 0,
      commentFindings: 0,
      findingsStorageUri: null,
      failureMessage: null,
      hasSourceTelemetry: false,
    },
    findings: {
      codeReviewRecordId: "synthetic_123",
      codeReviewCreatedAtUtc: new Date("2026-07-27T12:10:00.000Z"),
      noAgentsActivated: false,
      reviews: [],
      sourceTelemetry: null,
      ...findingsOverride,
    },
    inScopeFileCount: 1,
    outOfScopeFileCount: 0,
    reviewerCount: 1,
    ...responseOverrides,
  };
}
