/** Returns whether a live dataset case belongs to the selected pull request. */
export function datasetContainsPullRequest(
  cases: {
    pullRequestNumber: number | string;
    removedAtUtc?: Date | string | null;
  }[],
  pullRequestNumber: number | string,
) {
  return cases.some(
    (item) =>
      Number(item.pullRequestNumber) === Number(pullRequestNumber) &&
      !item.removedAtUtc,
  );
}

/** Runs promotion and leaves failures available for inline recovery. */
export async function runPromotion(
  promote: () => Promise<unknown>,
  onSuccess: () => void,
  onError: (message: string) => void,
) {
  try {
    await promote();
    onSuccess();
    return true;
  } catch (error) {
    onError(
      error instanceof Error
        ? error.message
        : "Reload the dataset and try again.",
    );
    return false;
  }
}
