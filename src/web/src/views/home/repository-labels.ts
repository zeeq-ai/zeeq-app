const maxRepositoryLabelLength = 17;
const repositoryLabelEdgeLength = 8;

export function repositoryLabel(displayName: string): string {
  // NOTE: Zeeq installs a GitHub App per organization, so metrics for one
  // Zeeq org only include repositories from one GitHub org. Drop the
  // redundant owner segment to keep chart labels readable.
  const slashIndex = displayName.indexOf("/");
  const repoName =
    slashIndex >= 0 && slashIndex < displayName.length - 1
      ? displayName.slice(slashIndex + 1)
      : displayName;
  return truncateMiddle(repoName);
}

function truncateMiddle(value: string): string {
  if (value.length <= maxRepositoryLabelLength) {
    return value;
  }

  return `${value.slice(0, repositoryLabelEdgeLength)}…${value.slice(-repositoryLabelEdgeLength)}`;
}
