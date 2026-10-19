/**
 * Helpers for awaiting a batch of requests to *settle* before acting on any
 * failure. `Promise.all` rejects on the first failure and leaves its siblings
 * running untracked; the metrics dashboard must not start a queued reload while
 * old requests are still in flight, so batches use `Promise.allSettled` and
 * surface the first rejection only once everything has finished.
 */

/** Returns the fulfilled value, or rethrows the rejection reason. */
export function unwrapSettled<T>(result: PromiseSettledResult<T>): T {
  if (result.status === "rejected") {
    throw result.reason;
  }
  return result.value;
}

/** Rethrows the first rejection (in input order) once all results are already settled. */
export function throwFirstRejection(
  results: readonly PromiseSettledResult<unknown>[],
): void {
  for (const result of results) {
    if (result.status === "rejected") {
      throw result.reason;
    }
  }
}
