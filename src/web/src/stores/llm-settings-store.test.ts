import { describe, expect, it } from "vitest";
import {
  isLlmProvider,
  llmModelCatalog,
  llmModelLabel,
  llmProviderCapabilities,
} from "./llm-settings-store";

describe("LLM provider catalog", () => {
  it("labels Gemini telemetry without exposing an unsupported chat provider", () => {
    expect(llmModelLabel("gemini-4-hydrogen")).toBe("Gemini 4 Hydrogen");
    expect(llmModelLabel("gemini-3.8-flash")).toBe("Gemini 3.8 Flash");
    expect(isLlmProvider("Google")).toBe(false);
    expect(Object.keys(llmModelCatalog)).not.toContain("Google");
  });

  it("provides capability flags for every selectable provider", () => {
    for (const provider of Object.keys(llmModelCatalog)) {
      expect(llmProviderCapabilities(provider)).toEqual(
        expect.objectContaining({
          requiresManagedKey: expect.any(Boolean),
          requiresEndpoint: expect.any(Boolean),
        }),
      );
    }
  });
});
