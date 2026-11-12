import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { MeResponse } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { useCodeReviewEvaluationStore } from "./code-review-evaluation-store";

const apiMocks = vi.hoisted(() => ({
  createDataset: vi.fn(),
  listDatasets: vi.fn(),
  getDataset: vi.fn(),
  createRun: vi.fn(),
  listRuns: vi.fn(),
  getRun: vi.fn(),
  resolvePrs: vi.fn(),
  refreshCase: vi.fn(),
}));

vi.mock("@/api/generated", () => ({
  CodeReviews: {},
  CodeReviewEvaluations: {
    createCodeReviewEvaluationDataset: apiMocks.createDataset,
    listCodeReviewEvaluationDatasets: apiMocks.listDatasets,
    getCodeReviewEvaluationDataset: apiMocks.getDataset,
    createCodeReviewEvaluationRun: apiMocks.createRun,
    listCodeReviewEvaluationRuns: apiMocks.listRuns,
    getCodeReviewEvaluationRun: apiMocks.getRun,
    resolveCodeReviewEvaluationPrTargets: apiMocks.resolvePrs,
    refreshCodeReviewEvaluationCase: apiMocks.refreshCase,
  },
}));

describe("code review evaluation run request keys", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
    apiMocks.listDatasets.mockResolvedValue({ items: [] });
    apiMocks.listRuns.mockResolvedValue({ items: [], nextCursorId: null });
    apiMocks.getRun.mockResolvedValue({ run: { id: "run_1" }, cases: [] });
  });

  it("reuses a failed identical request key and rotates it when the request changes", async () => {
    const store = useCodeReviewEvaluationStore();
    const request = {
      datasetId: "dataset_1",
      expectedVersion: 1,
      agentId: "agent_1",
      savedRevisionId: null,
      draft: null,
    };

    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_1", request);
    const firstKey = apiMocks.createRun.mock.calls[0][2].requestKey;

    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_1", request);
    expect(apiMocks.createRun.mock.calls[1][2].requestKey).toBe(firstKey);

    const changedRequest = { ...request, expectedVersion: 2 };
    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_1", changedRequest);
    const changedKey = apiMocks.createRun.mock.calls[2][2].requestKey;
    expect(changedKey).not.toBe(firstKey);

    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_2", changedRequest);
    const changedRepositoryKey = apiMocks.createRun.mock.calls[3][2].requestKey;
    expect(changedRepositoryKey).not.toBe(changedKey);

    useAppStore().user = { ...testUser(), organizationId: "org_456" };
    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_2", changedRequest);
    const changedOrganizationKey =
      apiMocks.createRun.mock.calls[4][2].requestKey;
    expect(changedOrganizationKey).not.toBe(changedRepositoryKey);

    apiMocks.createRun.mockResolvedValueOnce({
      id: "run_1",
      datasetId: "dataset_1",
    });
    await store.createRun("repo_2", changedRequest);
    expect(apiMocks.createRun.mock.calls[5][2].requestKey).toBe(
      changedOrganizationKey,
    );

    apiMocks.createRun.mockRejectedValueOnce(new Error("network timeout"));
    await store.createRun("repo_2", changedRequest);
    expect(apiMocks.createRun.mock.calls[6][2].requestKey).not.toBe(
      changedOrganizationKey,
    );
  });
});

describe("code review evaluation dataset request keys", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
    apiMocks.listDatasets.mockResolvedValue({ items: [] });
  });

  it("reuses a failed identical request key and rotates it for changed input or context", async () => {
    const store = useCodeReviewEvaluationStore();

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_1", " Dataset ", " Notes ");
    const firstKey = apiMocks.createDataset.mock.calls[0][2].createRequestKey;

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_1", "Dataset", "Notes");
    expect(apiMocks.createDataset.mock.calls[1][2].createRequestKey).toBe(firstKey);

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_1", "Dataset changed", "Notes");
    const changedInputKey = apiMocks.createDataset.mock.calls[2][2].createRequestKey;
    expect(changedInputKey).not.toBe(firstKey);

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    const changedRepositoryKey = apiMocks.createDataset.mock.calls[3][2].createRequestKey;
    expect(changedRepositoryKey).not.toBe(changedInputKey);

    useAppStore().user = { ...testUser(), organizationId: "org_456" };
    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    const changedOrganizationKey = apiMocks.createDataset.mock.calls[4][2].createRequestKey;
    expect(changedOrganizationKey).not.toBe(changedRepositoryKey);

    apiMocks.createDataset.mockResolvedValueOnce({ dataset: { id: "dataset_1" } });
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    expect(apiMocks.createDataset.mock.calls[5][2].createRequestKey).toBe(changedOrganizationKey);

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    expect(apiMocks.createDataset.mock.calls[6][2].createRequestKey).not.toBe(changedOrganizationKey);
  });
});

describe("evaluation pull request resolution scope", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("publishes only the latest dataset resolve and preserves its loading state", async () => {
    const first = deferred<{ items: { entry: string }[] }>();
    const second = deferred<{ items: { entry: string }[] }>();
    apiMocks.resolvePrs
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    const store = useCodeReviewEvaluationStore();

    const firstRequest = store.resolvePrs("repo_1", "dataset_A", 1, ["901"]);
    store.invalidateDatasetCaptureRequests();
    const secondRequest = store.resolvePrs("repo_1", "dataset_B", 2, ["902"]);

    first.resolve({ items: [{ entry: "901" }] });
    await firstRequest;
    expect(store.resolutionResults).toEqual([]);
    expect(store.resolvingPrs).toBe(true);

    second.resolve({ items: [{ entry: "902" }] });
    await expect(secondRequest).resolves.toEqual([{ entry: "902" }]);
    expect(store.resolutionResults).toEqual([{ entry: "902" }]);
    expect(store.resolvingPrs).toBe(false);
  });

  it("does not publish a late result after the editor closes on the same dataset", async () => {
    const pending = deferred<{ items: { entry: string }[] }>();
    apiMocks.resolvePrs.mockReturnValueOnce(pending.promise);
    const store = useCodeReviewEvaluationStore();

    const request = store.resolvePrs("repo_1", "dataset_A", 1, ["901"]);
    store.invalidateDatasetCaptureRequests();
    pending.resolve({ items: [{ entry: "901" }] });
    await request;

    expect(store.resolutionResults).toEqual([]);
    expect(store.resolvingPrs).toBe(false);
  });

  it("does not publish a refresh from a closed dataset after a newer capture starts", async () => {
    const closedDatasetRefresh = deferred<{ captureToken: string }>();
    const currentDatasetRefresh = deferred<{ captureToken: string }>();
    apiMocks.refreshCase
      .mockReturnValueOnce(closedDatasetRefresh.promise)
      .mockReturnValueOnce(currentDatasetRefresh.promise);
    const store = useCodeReviewEvaluationStore();

    const staleRequest = store.refreshCase("repo_1", "dataset_A", "case_A");
    store.invalidateDatasetCaptureRequests();
    const currentRequest = store.refreshCase("repo_1", "dataset_B", "case_B");

    closedDatasetRefresh.resolve({ captureToken: "stale-token" });
    await expect(staleRequest).resolves.toBeNull();
    expect(store.refreshCaptures).toEqual({});

    currentDatasetRefresh.resolve({ captureToken: "current-token" });
    await expect(currentRequest).resolves.toEqual({ captureToken: "current-token" });
    expect(store.refreshCaptures).toEqual({
      case_B: { captureToken: "current-token" },
    });
  });
});

describe("evaluation dataset loading scope", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("keeps a late dataset response from replacing the latest selection", async () => {
    const first = deferred<{ dataset: { id: string } }>();
    const second = deferred<{ dataset: { id: string } }>();
    apiMocks.getDataset
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    const store = useCodeReviewEvaluationStore();

    const firstRequest = store.loadDataset("repo_1", "dataset_A");
    const secondRequest = store.loadDataset("repo_1", "dataset_B");
    second.resolve({ dataset: { id: "dataset_B" } });
    await expect(secondRequest).resolves.toEqual({ id: "dataset_B" });
    first.resolve({ dataset: { id: "dataset_A" } });
    await expect(firstRequest).resolves.toBeNull();

    expect(store.selectedDataset?.id).toBe("dataset_B");
    expect(store.loadingDataset).toBe(false);
  });
});

function testUser(): MeResponse {
  return {
    userId: "usr_caller",
    subject: "usr_caller",
    organizationId: "org_123",
    teamId: null,
    provider: "test",
    providerSubject: "usr_caller",
    name: "Caller",
    email: "caller@example.com",
    pictureUrl: null,
    organizationRole: "member",
    organizationSlug: "test-org",
    organizations: [],
    aliases: [],
    isSystemAdmin: false,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((innerResolve) => {
    resolve = innerResolve;
  });
  return { promise, resolve };
}
