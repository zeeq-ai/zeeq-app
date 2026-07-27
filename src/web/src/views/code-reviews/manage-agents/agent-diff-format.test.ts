import { describe, expect, it } from "vitest";
import {
  codeReviewFileNameMatchTypeEnum,
  codeReviewModelTierEnum,
} from "@/api/generated";
import type { CodeReviewerAgentForm } from "@/stores/code-review-store";

import { formatAgentFormForDiff } from "./agent-diff-format";

describe("formatAgentFormForDiff", () => {
  it("includes metadata, status, activation filters, and prompt", () => {
    const form: CodeReviewerAgentForm = {
      displayName: "Logical Correctness",
      reviewFacet: "Logical",
      modelTier: codeReviewModelTierEnum.Max,
      enabled: false,
      prompt: "Review the PR for correctness.",
      activationConfiguration: {
        includedFiles: [
          {
            matchType: codeReviewFileNameMatchTypeEnum.Extension,
            pattern: ".ts",
          },
        ],
        excludedFiles: [
          {
            matchType: codeReviewFileNameMatchTypeEnum.Glob,
            pattern: "*.generated.ts",
          },
        ],
      },
    };

    const text = formatAgentFormForDiff(form);

    expect(text).toContain("Display name: Logical Correctness");
    expect(text).toContain("Facet: Logical");
    expect(text).toContain("Model tier: Max");
    expect(text).toContain("Status: Disabled");
    expect(text).toContain("- Extension: .ts");
    expect(text).toContain("- Glob: *.generated.ts");
    expect(text).toContain("Review the PR for correctness.");
  });
});
