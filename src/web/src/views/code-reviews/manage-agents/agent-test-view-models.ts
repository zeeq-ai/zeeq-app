import {
  codeReviewFindingLevelEnum,
  pullRequestStateEnum,
  type CodeReviewAgentTestRunResponse,
  type CodeReviewFindingDto,
  type CodeReviewFindingLevel,
  type CodeReviewPullRequestDto,
  type CodeReviewReviewerFindingsDto,
  type PullRequestState,
} from "@/api/generated";

export type AgentTestTargetRow = {
  value: string;
  pullRequest: CodeReviewPullRequestDto;
  label: string;
  description: string;
  repo: string;
  authorLogin: string;
  isDraft: boolean;
  stateLabel: string;
  stateColor: "success" | "info" | "neutral";
  icon: string;
  iconClass: string;
};

export type AgentTestSummaryMetric = {
  label: string;
  value: string;
};

export type AgentTestSeverityTab = {
  label: string;
  value: string;
  level: CodeReviewFindingLevel;
  count: number;
  color: "error" | "warning" | "neutral" | "info" | "tertiary";
  disabled: boolean;
};

export type ReviewerFindingSection = {
  reviewer: CodeReviewReviewerFindingsDto;
  findings: CodeReviewFindingDto[];
};

export const agentTestSeverityLevels: CodeReviewFindingLevel[] = [
  codeReviewFindingLevelEnum.Critical,
  codeReviewFindingLevelEnum.Major,
  codeReviewFindingLevelEnum.Minor,
  codeReviewFindingLevelEnum.Suggestion,
  codeReviewFindingLevelEnum.Comment,
];

/** Projects PR DTOs into the compact UListbox rows rendered by AgentTestPanel. */
export function buildAgentTestTargetRows(
  targets: CodeReviewPullRequestDto[],
): AgentTestTargetRow[] {
  return targets.map((target) => ({
    value: agentTestTargetValue(target),
    pullRequest: target,
    label: `#${target.pullRequestNumber} ${target.title}`,
    description: `${bareRepoName(target.ownerQualifiedRepoName)} · ${target.authorLogin} · ${target.branch} -> ${target.baseBranch} · updated ${formatDate(target.updatedAtUtc)}`,
    repo: target.ownerQualifiedRepoName,
    authorLogin: target.authorLogin,
    isDraft: target.isDraft,
    stateLabel: target.state,
    stateColor: pullRequestStateColor(target.state),
    icon: target.isDraft
      ? "i-hugeicons-edit-02"
      : "i-hugeicons-git-pull-request",
    iconClass: target.isDraft ? "text-warning" : "text-muted",
  }));
}

/** Stable listbox identity for PR rows and parent-owned selected PR state. */
export function agentTestTargetValue(target: CodeReviewPullRequestDto): string {
  return `${target.id}:${new Date(target.createdAtUtc).toISOString()}`;
}

/** Builds the compact numeric summary above the Results tab content. */
export function buildAgentTestSummaryMetrics(
  result: CodeReviewAgentTestRunResponse | null,
): AgentTestSummaryMetric[] {
  return [
    {
      label: "Files in scope",
      value: `${toNumber(result?.inScopeFileCount)}`,
    },
    {
      label: "Files filtered out",
      value: `${toNumber(result?.outOfScopeFileCount)}`,
    },
    {
      label: "Run time",
      value: formatRunTime(
        result?.review.createdAtUtc,
        result?.review.updatedAtUtc,
      ),
    },
  ];
}

/** Groups returned findings by severity without losing reviewer/facet context. */
export function buildReviewerSectionsByLevel(
  result: CodeReviewAgentTestRunResponse | null,
): Record<CodeReviewFindingLevel, ReviewerFindingSection[]> {
  const sectionsByLevel = emptyReviewerSectionsByLevel();

  for (const reviewer of result?.findings.reviews ?? []) {
    for (const level of agentTestSeverityLevels) {
      const findings = reviewer.findings.filter(
        (finding) => finding.level === level,
      );

      if (findings.length > 0) {
        sectionsByLevel[level].push({ reviewer, findings });
      }
    }
  }

  return sectionsByLevel;
}

export function buildAgentTestSeverityTabs(
  sectionsByLevel: Record<CodeReviewFindingLevel, ReviewerFindingSection[]>,
): AgentTestSeverityTab[] {
  return [
    severityTab(
      "Critical",
      "critical",
      codeReviewFindingLevelEnum.Critical,
      "error",
      sectionsByLevel,
    ),
    severityTab(
      "Major",
      "major",
      codeReviewFindingLevelEnum.Major,
      "warning",
      sectionsByLevel,
    ),
    severityTab(
      "Minor",
      "minor",
      codeReviewFindingLevelEnum.Minor,
      "neutral",
      sectionsByLevel,
    ),
    severityTab(
      "Suggestions",
      "suggestions",
      codeReviewFindingLevelEnum.Suggestion,
      "info",
      sectionsByLevel,
    ),
    severityTab(
      "Comments",
      "comments",
      codeReviewFindingLevelEnum.Comment,
      "tertiary",
      sectionsByLevel,
    ),
  ];
}

export function totalAgentTestFindingCount(
  tabs: AgentTestSeverityTab[],
): number {
  return tabs.reduce((total, item) => total + item.count, 0);
}

export function agentTestLocationLabel(finding: CodeReviewFindingDto): string {
  const line = finding.line ? `:${finding.line}` : "";
  const side = finding.side ? ` (${finding.side})` : "";

  return `${finding.file}${line}${side}`;
}

function severityTab(
  label: string,
  value: string,
  level: CodeReviewFindingLevel,
  color: AgentTestSeverityTab["color"],
  sectionsByLevel: Record<CodeReviewFindingLevel, ReviewerFindingSection[]>,
): AgentTestSeverityTab {
  const count = sectionsByLevel[level].reduce(
    (count, section) => count + section.findings.length,
    0,
  );

  return {
    label,
    value,
    level,
    color,
    count,
    disabled: count === 0,
  };
}

function emptyReviewerSectionsByLevel(): Record<
  CodeReviewFindingLevel,
  ReviewerFindingSection[]
> {
  return {
    [codeReviewFindingLevelEnum.Critical]: [],
    [codeReviewFindingLevelEnum.Major]: [],
    [codeReviewFindingLevelEnum.Minor]: [],
    [codeReviewFindingLevelEnum.Suggestion]: [],
    [codeReviewFindingLevelEnum.Comment]: [],
  };
}

function pullRequestStateColor(
  state: PullRequestState,
): AgentTestTargetRow["stateColor"] {
  if (state === pullRequestStateEnum.Merged) {
    return "success";
  }

  return state === pullRequestStateEnum.Open ? "info" : "neutral";
}

function bareRepoName(ownerQualifiedRepoName: string): string {
  const slashIndex = ownerQualifiedRepoName.indexOf("/");
  return slashIndex >= 0 && slashIndex < ownerQualifiedRepoName.length - 1
    ? ownerQualifiedRepoName.slice(slashIndex + 1)
    : ownerQualifiedRepoName;
}

function formatDate(value: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
  }).format(new Date(value));
}

function formatRunTime(
  startedAtUtc: Date | null | undefined,
  completedAtUtc: Date | null | undefined,
): string {
  if (!startedAtUtc || !completedAtUtc) {
    return "0s";
  }

  const elapsedMs =
    new Date(completedAtUtc).getTime() - new Date(startedAtUtc).getTime();
  if (!Number.isFinite(elapsedMs) || elapsedMs <= 0) {
    return "0s";
  }

  if (elapsedMs < 1000) {
    return `${elapsedMs}ms`;
  }

  const elapsedSeconds = Math.round(elapsedMs / 1000);
  if (elapsedSeconds < 60) {
    return `${elapsedSeconds}s`;
  }

  const minutes = Math.floor(elapsedSeconds / 60);
  const seconds = elapsedSeconds % 60;
  return seconds === 0 ? `${minutes}m` : `${minutes}m ${seconds}s`;
}

function toNumber(value: number | string | null | undefined): number {
  const numeric = Number(value ?? 0);
  return Number.isFinite(numeric) ? numeric : 0;
}
