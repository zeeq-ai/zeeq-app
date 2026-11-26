import { describe, expect, it, vi } from "vitest";
import { focusFinding } from "./focusFinding";
describe("finding deep-link focus", () => {
  it("focuses the target proposal within the current review", () => {
    const root = document.createElement("div");
    root.innerHTML =
      '<article data-occurrence-key="F1" tabindex="-1"><button data-proposal-confirm>Confirm</button></article><article data-occurrence-key="F2" tabindex="-1"><button data-proposal-confirm>Confirm</button></article>';
    document.body.append(root);
    const article = root.querySelector<HTMLElement>(
      '[data-occurrence-key="F2"]',
    )!;
    article.scrollIntoView = vi.fn();
    focusFinding(root, "F2");
    expect(document.activeElement).toBe(article.querySelector("button"));
    expect(article.scrollIntoView).toHaveBeenCalledOnce();
    expect(article.classList.contains("ring-primary")).toBe(true);
    root.remove();
  });
});
