import { describe, expect, it, vi } from "vitest";
import { datasetContainsPullRequest, runPromotion } from "./promotion-flow";

describe("promotion flow", () => {
  it("accepts only active cases for the current PR", () => {
    expect(
      datasetContainsPullRequest(
        [{ pullRequestNumber: "910", removedAtUtc: null }],
        910,
      ),
    ).toBe(true);
    expect(
      datasetContainsPullRequest(
        [{ pullRequestNumber: 910, removedAtUtc: new Date() }],
        910,
      ),
    ).toBe(false);
    expect(datasetContainsPullRequest([{ pullRequestNumber: 911 }], 910)).toBe(
      false,
    );
  });

  it("reports successful promotion and runs success handling", async () => {
    const promote = vi.fn().mockResolvedValue(undefined);
    const onSuccess = vi.fn();
    const onError = vi.fn();
    await expect(runPromotion(promote, onSuccess, onError)).resolves.toBe(true);
    expect(promote).toHaveBeenCalledOnce();
    expect(onSuccess).toHaveBeenCalledOnce();
    expect(onError).not.toHaveBeenCalled();
  });

  it("surfaces promotion errors for inline recovery", async () => {
    const onSuccess = vi.fn();
    const onError = vi.fn();
    await expect(
      runPromotion(
        () => Promise.reject(new Error("Dataset version changed")),
        onSuccess,
        onError,
      ),
    ).resolves.toBe(false);
    expect(onError).toHaveBeenCalledWith("Dataset version changed");
    expect(onSuccess).not.toHaveBeenCalled();
  });
});
