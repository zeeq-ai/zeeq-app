import { describe, expect, it } from "vitest";
import { getInboxDatasetRowState } from "./inbox-add-dataset-view-models";

describe("inbox dataset selection", () => {
  it("allows a full row to load membership before deciding whether it can add", () => {
    expect(
      getInboxDatasetRowState({
        caseCount: 25,
        maxCases: 25,
        containsPullRequest: false,
        mutating: false,
      }),
    ).toEqual({ full: true, selectable: true, canAdd: false });
  });

  it("makes a known existing case openable but not selectable for another add", () => {
    expect(
      getInboxDatasetRowState({
        caseCount: 25,
        maxCases: 25,
        containsPullRequest: true,
        mutating: false,
      }),
    ).toEqual({ full: true, selectable: false, canAdd: false });
  });

  it("locks dataset changes during add or create", () => {
    expect(
      getInboxDatasetRowState({
        caseCount: 2,
        maxCases: 25,
        containsPullRequest: false,
        mutating: true,
      }),
    ).toEqual({ full: false, selectable: false, canAdd: false });
  });
});
