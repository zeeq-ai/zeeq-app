export function getInboxDatasetRowState(input: {
  caseCount: number;
  maxCases: number;
  containsPullRequest: boolean;
  mutating: boolean;
}) {
  const full = input.caseCount >= input.maxCases;
  // NOTE: A fresh membership read also confirms an uncertain add. Existing cases
  // expose Open case; they do not need another mutating action to confirm success.
  return {
    full,
    selectable: !input.mutating && !input.containsPullRequest,
    canAdd: !input.mutating && !full && !input.containsPullRequest,
  };
}
