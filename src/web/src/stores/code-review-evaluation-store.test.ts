import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type {
  CodeReviewEvaluationDatasetDetailDto,
  MeResponse,
} from "@/api/generated";
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
  searchTargets: vi.fn(),
  addInboxCase: vi.fn(),
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
    listCodeReviewEvaluationPrTargets: apiMocks.searchTargets,
    addCodeReviewEvaluationCase: apiMocks.addInboxCase,
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
    expect(apiMocks.createDataset.mock.calls[1][2].createRequestKey).toBe(
      firstKey,
    );

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_1", "Dataset changed", "Notes");
    const changedInputKey =
      apiMocks.createDataset.mock.calls[2][2].createRequestKey;
    expect(changedInputKey).not.toBe(firstKey);

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    const changedRepositoryKey =
      apiMocks.createDataset.mock.calls[3][2].createRequestKey;
    expect(changedRepositoryKey).not.toBe(changedInputKey);

    useAppStore().user = { ...testUser(), organizationId: "org_456" };
    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    const changedOrganizationKey =
      apiMocks.createDataset.mock.calls[4][2].createRequestKey;
    expect(changedOrganizationKey).not.toBe(changedRepositoryKey);

    apiMocks.createDataset.mockResolvedValueOnce({
      dataset: { id: "dataset_1" },
    });
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    expect(apiMocks.createDataset.mock.calls[5][2].createRequestKey).toBe(
      changedOrganizationKey,
    );

    apiMocks.createDataset.mockRejectedValueOnce(new Error("network timeout"));
    await store.createDataset("repo_2", "Dataset changed", "Notes");
    expect(apiMocks.createDataset.mock.calls[6][2].createRequestKey).not.toBe(
      changedOrganizationKey,
    );
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
    await expect(currentRequest).resolves.toEqual({
      captureToken: "current-token",
    });
    expect(store.refreshCaptures).toEqual({
      case_B: { captureToken: "current-token" },
    });
  });
});

describe("inbox add-to-dataset mutation", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("keeps the submitted version for retries after an uncertain failure", async () => {
    const store = useCodeReviewEvaluationStore();
    store.setInboxAddScope("org_123/repo_1/pr_901");
    apiMocks.addInboxCase.mockRejectedValueOnce(new Error("network timeout"));

    await expect(
      store.addInboxPullRequest("repo_1", "dataset_1", 901, 4),
    ).resolves.toBeNull();
    expect(store.inboxAddError).toBe(
      "Could not confirm whether this pull request was added. Retry to check.",
    );

    apiMocks.addInboxCase.mockResolvedValueOnce({
      added: false,
      datasetId: "dataset_1",
      datasetVersion: 5,
      pullRequestNumber: 901,
      baseSha: "base",
      headSha: "head",
      capturedAtUtc: new Date(),
    });
    await store.addInboxPullRequest("repo_1", "dataset_1", 901, 4);
    expect(apiMocks.addInboxCase.mock.calls.map((call) => call[3])).toEqual([
      { pullRequestNumber: 901, expectedVersion: 4 },
      { pullRequestNumber: 901, expectedVersion: 4 },
    ]);
  });

  it("ignores an add response after the picker changes scope", async () => {
    const pending = deferred<{
      added: boolean;
      datasetId: string;
      datasetVersion: number;
      pullRequestNumber: number;
      baseSha: string;
      headSha: string;
      capturedAtUtc: Date;
    }>();
    apiMocks.addInboxCase.mockReturnValueOnce(pending.promise);
    const store = useCodeReviewEvaluationStore();
    store.setInboxAddScope("org_123/repo_1/pr_901");

    const request = store.addInboxPullRequest("repo_1", "dataset_1", 901, 4);
    store.invalidateInboxAddRequests();
    pending.resolve({
      added: true,
      datasetId: "dataset_1",
      datasetVersion: 5,
      pullRequestNumber: 901,
      baseSha: "base",
      headSha: "head",
      capturedAtUtc: new Date(),
    });

    await expect(request).resolves.toBeNull();
    expect(store.inboxAddError).toBeNull();
    expect(store.addingInboxCase).toBe(false);
  });

  it("does not publish a create response after the picker closes", async () => {
    const pendingCreate = deferred<{
      dataset: {
        id: string;
        repositoryId: string;
        name: string;
        description: null;
        version: number;
        caseCount: number;
        createdAtUtc: Date;
        updatedAtUtc: Date;
        archivedAtUtc: null;
        cases: [];
      };
    }>();
    apiMocks.createDataset.mockReturnValueOnce(pendingCreate.promise);
    const store = useCodeReviewEvaluationStore();
    store.setInboxAddScope("org_123/repo_1/pr_901");

    const request = store.createInboxDataset("repo_1", "New dataset");
    store.invalidateInboxAddRequests();
    const createdAtUtc = new Date("2026-11-13T12:00:00Z");
    pendingCreate.resolve({
      dataset: {
        id: "created_1",
        repositoryId: "repo_1",
        name: "New dataset",
        description: null,
        version: 1,
        caseCount: 0,
        createdAtUtc,
        updatedAtUtc: createdAtUtc,
        archivedAtUtc: null,
        cases: [],
      },
    });

    await expect(request).resolves.toBeNull();
    expect(store.inboxAddDataset).toBeNull();
    expect(store.inboxAddDatasets).toEqual([]);
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

  it("keeps the current selection when an inbox draft changes during dataset lookup", async () => {
    const pending = deferred<{ dataset: { id: string } }>();
    apiMocks.getDataset.mockReturnValueOnce(pending.promise);
    const store = useCodeReviewEvaluationStore();
    const originalDataset = {
      id: "dataset_original",
    } as unknown as CodeReviewEvaluationDatasetDetailDto;
    store.selectedDataset = originalDataset;
    let draftEditEpoch = 0;

    const request = store.loadDataset(
      "repo_1",
      "dataset_target",
      () => draftEditEpoch === 0,
    );
    draftEditEpoch += 1;
    pending.resolve({ dataset: { id: "dataset_target" } });

    await expect(request).resolves.toBeNull();
    expect(store.selectedDataset?.id).toBe("dataset_original");
  });
});

describe("inbox dataset creation", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("keeps the created summary when an older dataset list settles later", async () => {
    const pendingList = deferred<{ items: [] }>();
    apiMocks.listDatasets.mockReturnValueOnce(pendingList.promise);
    const createdAtUtc = new Date("2026-11-13T12:00:00Z");
    apiMocks.createDataset.mockResolvedValueOnce({
      dataset: {
        id: "dataset_created",
        repositoryId: "repo_1",
        name: "New dataset",
        description: null,
        version: 1,
        caseCount: 0,
        createdAtUtc,
        updatedAtUtc: createdAtUtc,
        archivedAtUtc: null,
        cases: [],
      },
    });
    const store = useCodeReviewEvaluationStore();
    store.setInboxAddScope("org_123/repo_1/pr_901");
    const listRequest = store.loadInboxAddDatasets("repo_1");
    expect(store.loadingInboxAddDatasets).toBe(true);

    await store.createInboxDataset("repo_1", "New dataset");
    expect(store.loadingInboxAddDatasets).toBe(false);
    pendingList.resolve({ items: [] });
    await listRequest;

    expect(store.inboxAddDatasets.map((item) => item.id)).toEqual([
      "dataset_created",
    ]);
    expect(store.inboxAddDataset?.id).toBe("dataset_created");
  });

  it("publishes the created summary without waiting for a dataset list refresh", async () => {
    const createdAtUtc = new Date("2026-11-13T12:00:00Z");
    apiMocks.createDataset.mockResolvedValueOnce({
      dataset: {
        id: "dataset_created",
        repositoryId: "repo_1",
        name: "New dataset",
        description: null,
        version: 1,
        caseCount: 0,
        createdAtUtc,
        updatedAtUtc: createdAtUtc,
        archivedAtUtc: null,
        cases: [],
      },
    });
    const store = useCodeReviewEvaluationStore();
    store.setInboxAddScope("org_123/repo_1/pr_901");

    await expect(
      store.createInboxDataset("repo_1", "New dataset"),
    ).resolves.toMatchObject({ id: "dataset_created" });

    expect(apiMocks.listDatasets).not.toHaveBeenCalled();
    expect(store.inboxAddDatasets).toEqual([
      expect.objectContaining({
        id: "dataset_created",
        name: "New dataset",
        version: 1,
        caseCount: 0,
      }),
    ]);
  });
});

describe("evaluation PR target search scope", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("does not let a late old query replace rows or clear the newer search state", async () => {
    const first = deferred<{
      items: { pullRequestNumber: number }[];
      nextCursor: string | null;
    }>();
    const second = deferred<{
      items: { pullRequestNumber: number }[];
      nextCursor: string | null;
    }>();
    apiMocks.searchTargets
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    const store = useCodeReviewEvaluationStore();

    const firstSearch = store.searchTargets(
      "repo_1",
      "old query",
      undefined,
      false,
      "dataset_A",
    );
    const secondSearch = store.searchTargets(
      "repo_1",
      "new query",
      undefined,
      false,
      "dataset_B",
    );

    first.resolve({ items: [{ pullRequestNumber: 901 }], nextCursor: "old" });
    await firstSearch;
    expect(store.targetRows).toEqual([]);
    expect(store.targetNextCursor).toBeNull();
    expect(store.loadingTargets).toBe(true);

    second.resolve({ items: [{ pullRequestNumber: 902 }], nextCursor: "next" });
    await secondSearch;
    expect(store.targetRows).toEqual([{ pullRequestNumber: 902 }]);
    expect(store.targetNextCursor).toBe("next");
    expect(store.loadingTargets).toBe(false);
    expect(store.loadingMoreTargets).toBe(false);
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
