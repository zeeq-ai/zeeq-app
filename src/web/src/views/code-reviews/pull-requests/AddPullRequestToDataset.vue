<template>
  <div
    v-if="embedded && open"
    class="flex h-full min-h-0 flex-col overflow-hidden"
  >
    <InboxAddDatasetPickerContent
      :pull-request
      :embedded="!!embedded"
      :datasets="visibleDatasets"
      :selected-dataset-id="selectedDatasetId"
      :search
      :loading-datasets="loadingInboxAddDatasets"
      :loading-selected-dataset="loadingInboxAddDataset"
      :creating-new
      :creating-dataset="creatingInboxDataset"
      :new-dataset-name
      :error="inboxAddError"
      :error-code="inboxAddErrorCode"
      :adding="addingInboxCase"
      :can-add
      :can-open-editor="canOpenEditor"
      :add-label
      :max-cases
      :selected-dataset
      :version-for
      @back="back"
      @cancel="close"
      @submit="addPullRequest"
      @select="selectDataset"
      @update:search="search = $event"
      @update:new-dataset-name="newDatasetName = $event"
      @start-create="creatingNew = true"
      @cancel-create="cancelCreate"
      @create="createDataset"
      @reload-version="reloadVersion"
      @open-editor="openEditor"
    />
  </div>

  <USlideover
    v-else-if="!embedded"
    v-model:open="open"
    title="Add to dataset"
    :ui="{ content: 'max-w-md' }"
  >
    <template #content>
      <InboxAddDatasetPickerContent
        :pull-request
        :embedded="!!embedded"
        :datasets="visibleDatasets"
        :selected-dataset-id="selectedDatasetId"
        :search
        :loading-datasets="loadingInboxAddDatasets"
        :loading-selected-dataset="loadingInboxAddDataset"
        :creating-new
        :creating-dataset="creatingInboxDataset"
        :new-dataset-name
        :error="inboxAddError"
        :error-code="inboxAddErrorCode"
        :adding="addingInboxCase"
        :can-add
        :can-open-editor="canOpenEditor"
        :add-label
        :max-cases
        :selected-dataset
        :version-for
        @back="back"
        @cancel="close"
        @submit="addPullRequest"
        @select="selectDataset"
        @update:search="search = $event"
        @update:new-dataset-name="newDatasetName = $event"
        @start-create="creatingNew = true"
        @cancel-create="cancelCreate"
        @create="createDataset"
        @reload-version="reloadVersion"
        @open-editor="openEditor"
      />
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import type {
  CodeReviewEvaluationDatasetSummaryDto,
  CodeReviewPullRequestDto,
} from "@/api/generated";
import { useCodeReviewEvaluationStore } from "@/stores/code-review-evaluation-store";
import InboxAddDatasetPickerContent from "./InboxAddDatasetPickerContent.vue";
import { getInboxDatasetRowState } from "./inbox-add-dataset-view-models";

const props = defineProps<{
  pullRequest: CodeReviewPullRequestDto;
  repositoryId: string;
  embedded?: boolean;
  maxCases: number;
  canOpenEditor: boolean;
}>();

const open = defineModel<boolean>("open", { default: false });
const emits = defineEmits<{
  back: [];
  added: [
    result: {
      added: boolean;
      datasetId: string;
      repositoryId: string;
      datasetName: string;
      datasetVersion: number;
      pullRequestNumber: number;
      baseSha: string;
      headSha: string;
      capturedAtUtc: Date;
    },
  ];
  openEditor: [datasetId: string, pullRequestNumber: number];
}>();

const store = useCodeReviewEvaluationStore();
const {
  inboxAddDatasets,
  inboxAddDataset,
  loadingInboxAddDatasets,
  loadingInboxAddDataset,
  addingInboxCase,
  creatingInboxDataset,
  inboxAddError,
  inboxAddErrorCode,
} = storeToRefs(store);

const search = ref("");
const selectedDatasetId = ref<string | null>(null);
const creatingNew = ref(false);
const newDatasetName = ref("");
const expectedVersions = ref<Record<string, number>>({});
const attemptedDatasetIds = ref<string[]>([]);
const selectedDataset = computed(() =>
  inboxAddDataset.value?.id === selectedDatasetId.value
    ? inboxAddDataset.value
    : null,
);
const activeContext = computed(() =>
  JSON.stringify([
    store.activeOrganizationId,
    props.repositoryId,
    props.pullRequest.id,
    props.pullRequest.pullRequestNumber,
  ]),
);
const visibleDatasets = computed(() => {
  const query = search.value.trim().toLocaleLowerCase();
  return inboxAddDatasets.value
    .filter((item) => !item.archivedAtUtc)
    .filter((item) => !query || item.name.toLocaleLowerCase().includes(query));
});
const selectedSummary = computed(
  () =>
    inboxAddDatasets.value.find(
      (item) => item.id === selectedDatasetId.value,
    ) ?? null,
);
const selectedVersion = computed(() => {
  const datasetId = selectedDatasetId.value;
  if (!datasetId) return null;
  return (
    expectedVersions.value[datasetId] ??
    (selectedDataset.value
      ? Number(selectedDataset.value.version)
      : selectedSummary.value
        ? Number(selectedSummary.value.version)
        : null)
  );
});
const canAdd = computed(() => {
  const state = getInboxDatasetRowState({
    caseCount: Number(selectedSummary.value?.caseCount ?? 0),
    maxCases: props.maxCases,
    containsPullRequest: containsCurrentPullRequest.value,
    mutating: creatingInboxDataset.value || addingInboxCase.value,
  });
  return (
    !!selectedDatasetId.value &&
    !!selectedDataset.value &&
    state.canAdd &&
    !loadingInboxAddDataset.value
  );
});
const containsCurrentPullRequest = computed(
  () =>
    selectedDataset.value?.cases.some(
      (item) =>
        Number(item.pullRequestNumber) === currentPullRequestNumber.value &&
        !item.removedAtUtc,
    ) ?? false,
);
const nextVersion = computed(() => (selectedVersion.value ?? 0) + 1);
const addLabel = computed(() => `Add & save as v${nextVersion.value}`);
const currentPullRequestNumber = computed(() =>
  Number(props.pullRequest.pullRequestNumber),
);
let selectionRequestId = 0;
let activeContextKey: string | null = null;
let preserveOnClose = false;

watch(
  () => [open.value, activeContext.value] as const,
  async ([isOpen, context]) => {
    if (!isOpen) {
      store.invalidateInboxAddRequests();
      if (preserveOnClose) {
        preserveOnClose = false;
        return;
      }
      activeContextKey = null;
      return;
    }
    if (activeContextKey !== context) {
      activeContextKey = context;
      store.setInboxAddScope(context);
      selectedDatasetId.value = null;
      expectedVersions.value = {};
      attemptedDatasetIds.value = [];
      creatingNew.value = false;
      newDatasetName.value = "";
      inboxAddDataset.value = null;
    } else {
      store.setInboxAddScope(context);
    }
    const datasets = await store.loadInboxAddDatasets(props.repositoryId);
    if (!open.value || activeContextKey !== context) return;
    if (
      selectedDatasetId.value &&
      datasets.some((item) => item.id === selectedDatasetId.value)
    ) {
      await loadSelectedDataset(selectedDatasetId.value);
    }
  },
  { immediate: true },
);

watch(
  activeContext,
  (next, previous) => {
    if (next !== previous && open.value) {
      open.value = false;
    }
  },
  { flush: "sync" },
);

function versionFor(dataset: CodeReviewEvaluationDatasetSummaryDto) {
  if (
    dataset.id === selectedDatasetId.value &&
    selectedVersion.value !== null
  ) {
    return selectedVersion.value;
  }
  return Number(dataset.version);
}

async function selectDataset(datasetId: string) {
  selectedDatasetId.value = datasetId;
  inboxAddDataset.value = null;
  inboxAddError.value = null;
  inboxAddErrorCode.value = null;
  if (!attemptedDatasetIds.value.includes(datasetId)) {
    const summary = inboxAddDatasets.value.find(
      (item) => item.id === datasetId,
    );
    if (summary) expectedVersions.value[datasetId] = Number(summary.version);
  }
  await loadSelectedDataset(datasetId);
}

async function loadSelectedDataset(datasetId: string) {
  const requestId = ++selectionRequestId;
  const context = activeContextKey;
  const loaded = await store.loadInboxAddDataset(props.repositoryId, datasetId);
  if (
    requestId !== selectionRequestId ||
    !open.value ||
    context !== activeContextKey ||
    datasetId !== selectedDatasetId.value ||
    !loaded
  ) {
    return;
  }
  if (!attemptedDatasetIds.value.includes(datasetId)) {
    expectedVersions.value[datasetId] = Number(loaded.version);
  }
}

async function createDataset() {
  if (!newDatasetName.value.trim()) return;
  const context = activeContextKey;
  const created = await store.createInboxDataset(
    props.repositoryId,
    newDatasetName.value,
  );
  if (!created || !open.value || context !== activeContextKey) return;
  selectedDatasetId.value = created.id;
  expectedVersions.value[created.id] = Number(created.version);
  attemptedDatasetIds.value = attemptedDatasetIds.value.filter(
    (item) => item !== created.id,
  );
  newDatasetName.value = "";
  creatingNew.value = false;
}

function cancelCreate() {
  creatingNew.value = false;
  newDatasetName.value = "";
}

async function reloadVersion() {
  const datasetId = selectedDatasetId.value;
  if (!datasetId) return;
  const loaded = await store.loadInboxAddDataset(props.repositoryId, datasetId);
  if (!loaded || !open.value || selectedDatasetId.value !== datasetId) return;
  expectedVersions.value[datasetId] = Number(loaded.version);
  attemptedDatasetIds.value = attemptedDatasetIds.value.filter(
    (item) => item !== datasetId,
  );
}

async function addPullRequest() {
  const datasetId = selectedDatasetId.value;
  const submittedPullRequestNumber = currentPullRequestNumber.value;
  const submittedRepositoryId = props.repositoryId;
  const submittedContext = activeContextKey;
  const expectedVersion = selectedVersion.value;
  if (!datasetId || expectedVersion === null || !canAdd.value) return;
  if (!attemptedDatasetIds.value.includes(datasetId)) {
    attemptedDatasetIds.value.push(datasetId);
  }
  const result = await store.addInboxPullRequest(
    props.repositoryId,
    datasetId,
    submittedPullRequestNumber,
    expectedVersion,
  );
  if (
    !result ||
    !open.value ||
    submittedContext !== activeContextKey ||
    selectedDatasetId.value !== datasetId ||
    result.datasetId !== datasetId ||
    Number(result.pullRequestNumber) !== submittedPullRequestNumber
  ) {
    return;
  }
  const datasetName =
    inboxAddDatasets.value.find((item) => item.id === datasetId)?.name ??
    selectedDataset.value?.name ??
    "dataset";
  const version = Number(result.datasetVersion);
  const currentCount = Number(
    inboxAddDatasets.value.find((item) => item.id === datasetId)?.caseCount ??
      0,
  );
  const nextCount = Math.min(
    props.maxCases,
    currentCount + (result.added ? 1 : 0),
  );
  inboxAddDatasets.value = inboxAddDatasets.value.map((item) =>
    item.id === datasetId ? { ...item, version, caseCount: nextCount } : item,
  );
  emits("added", {
    added: result.added,
    datasetId,
    repositoryId: submittedRepositoryId,
    datasetName,
    datasetVersion: version,
    pullRequestNumber: Number(result.pullRequestNumber),
    baseSha: result.baseSha,
    headSha: result.headSha,
    capturedAtUtc: result.capturedAtUtc,
  });
  close();
}

function openEditor(datasetId: string) {
  emits("openEditor", datasetId, currentPullRequestNumber.value);
}

function back() {
  preserveOnClose = true;
  emits("back");
  open.value = false;
}

function close() {
  open.value = false;
}
</script>
