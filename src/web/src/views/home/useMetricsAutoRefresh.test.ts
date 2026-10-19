import { describe, expect, it } from "vitest";
import { createCoalescingRunner } from "./useMetricsAutoRefresh";

/** A batch whose completion the test controls. */
function deferredBatch() {
  let resolve!: () => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** Builds a runner whose batches are test-controlled and which records overlap. */
function harness() {
  const batches: ReturnType<typeof deferredBatch>[] = [];
  let running = 0;
  let maxRunning = 0;
  const runner = createCoalescingRunner(async () => {
    const batch = deferredBatch();
    batches.push(batch);
    running++;
    maxRunning = Math.max(maxRunning, running);
    try {
      await batch.promise;
    } finally {
      running--;
    }
  });
  return { runner, batches, maxRunning: () => maxRunning };
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe("createCoalescingRunner", () => {
  it("runs immediately when idle", async () => {
    const { runner, batches } = harness();

    const first = runner.request();
    expect(batches).toHaveLength(1);
    batches[0].resolve();
    await first;

    expect(runner.isBusy()).toBe(false);
  });

  it("queues exactly one trailing batch, started only after the running batch settles", async () => {
    const { runner, batches, maxRunning } = harness();

    const first = runner.request();
    const second = runner.request();
    const third = runner.request();

    expect(batches).toHaveLength(1);
    expect(second).toBe(third);

    batches[0].resolve();
    await first;
    await flush();
    expect(batches).toHaveLength(2);

    batches[1].resolve();
    await second;

    expect(batches).toHaveLength(2);
    expect(maxRunning()).toBe(1);
  });

  it("starts the trailing batch even when the running batch rejects", async () => {
    const { runner, batches } = harness();

    const first = runner.request();
    const trailing = runner.request();
    batches[0].reject(new Error("panel failed"));

    await expect(first).rejects.toThrow("panel failed");
    await flush();
    expect(batches).toHaveLength(2);

    batches[1].resolve();
    await expect(trailing).resolves.toBeUndefined();
  });

  it("does not start a third overlapping batch from a request made while the queued one is being handed off", async () => {
    const { runner, batches, maxRunning } = harness();

    const first = runner.request();
    const queued = runner.request();
    // Registered after the trailing request was queued: runs in the microtask gap in which the
    // running batch has cleared but the queued batch has not started yet.
    const lateRequests: Promise<void>[] = [];
    void first.then(
      () => lateRequests.push(runner.request()),
      () => undefined,
    );

    batches[0].resolve();
    await first;
    await flush();

    expect(batches).toHaveLength(2);
    expect(lateRequests[0]).toBe(queued);

    batches[1].resolve();
    await queued;
    await flush();

    expect(batches).toHaveLength(2);
    expect(maxRunning()).toBe(1);
  });

  it("queues a new trailing batch for a request made while the trailing batch runs", async () => {
    const { runner, batches } = harness();

    const first = runner.request();
    const trailing = runner.request();
    batches[0].resolve();
    await first;
    await flush();

    const next = runner.request();
    expect(next).not.toBe(trailing);

    batches[1].resolve();
    await trailing;
    await flush();
    expect(batches).toHaveLength(3);

    batches[2].resolve();
    await next;
  });

  it("drops timer ticks while busy but runs them when idle", async () => {
    const { runner, batches } = harness();

    const first = runner.request();
    expect(runner.requestIfIdle()).toBeNull();

    batches[0].resolve();
    await first;

    const tick = runner.requestIfIdle();
    expect(tick).not.toBeNull();
    batches[1].resolve();
    await tick;
  });
});
