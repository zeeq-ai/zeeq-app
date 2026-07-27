import { describe, expect, it } from "vitest";

import { parsePreviewFilePaths } from "./file-filter-preview";

describe("parsePreviewFilePaths", () => {
  it("normalizes slash direction, trims blanks, and removes duplicate paths", () => {
    expect(
      parsePreviewFilePaths(`
        /src\\App.ts
        src/App.ts

        docs/readme.md
      `),
    ).toEqual(["src/App.ts", "docs/readme.md"]);
  });
});
