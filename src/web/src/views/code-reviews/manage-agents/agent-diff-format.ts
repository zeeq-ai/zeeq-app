import type { CodeReviewerAgentForm } from "@/stores/code-review-store";

/**
 * Formats the full reviewer-agent draft as stable text so the diff drawer shows
 * metadata, enablement, activation filters, and prompt changes together.
 */
export function formatAgentFormForDiff(form: CodeReviewerAgentForm): string {
  return [
    "# Reviewer agent",
    "",
    `Display name: ${form.displayName}`,
    `Facet: ${form.reviewFacet}`,
    `Model tier: ${form.modelTier}`,
    `Status: ${form.enabled ? "Enabled" : "Disabled"}`,
    "",
    "## Activation filters",
    "",
    "Included files:",
    ...formatPatterns(form.activationConfiguration.includedFiles),
    "",
    "Excluded files:",
    ...formatPatterns(form.activationConfiguration.excludedFiles),
    "",
    "## Prompt",
    "",
    form.prompt,
  ].join("\n");
}

function formatPatterns(
  patterns: CodeReviewerAgentForm["activationConfiguration"]["includedFiles"],
) {
  if (patterns.length === 0) {
    return ["- (none)"];
  }

  return patterns.map((item) => `- ${item.matchType}: ${item.pattern}`);
}
