<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <UEmpty
      v-if="!loadingRepositories && !hasConfiguredRepositories"
      icon="i-hugeicons-github"
      title="No repositories configured"
      description="Enable at least one GitHub repository before creating reviewer agents."
    >
      <template #actions>
        <UButton
          label="Open GitHub settings"
          icon="i-hugeicons-settings-01"
          color="neutral"
          to="/settings/github"
        />
      </template>
    </UEmpty>

    <template v-else>
      <div
        v-if="!canManageOrganization || codeReviewError"
        class="grid gap-2 border-b border-default p-4 sm:px-6"
      >
        <UAlert
          v-if="!canManageOrganization"
          title="View only"
          description="Only organization owners and admins can manage reviewer agents."
          icon="i-hugeicons-information-circle"
          color="neutral"
          variant="subtle"
        />
        <UAlert
          v-if="codeReviewError"
          title="Agent settings unavailable"
          :description="codeReviewError"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
        />
      </div>

      <div class="flex min-h-0 flex-1 overflow-hidden">
        <section
          class="flex min-h-0 basis-full flex-col border-r border-default lg:max-w-[28rem] lg:basis-[28rem]"
        >
          <div
            class="flex min-h-16 items-center justify-between gap-3 border-b border-default px-4 py-3 sm:px-6"
          >
            <div class="flex min-w-0 items-center gap-2">
              <h2 class="text-base font-semibold text-highlighted">Agents</h2>
              <UBadge
                :label="agents.length"
                color="primary"
                variant="subtle"
                class="rounded-full"
              />
            </div>
          </div>

          <div v-if="loadingAgents" class="grid gap-2 p-4 sm:px-6">
            <USkeleton
              v-for="index in 4"
              :key="index"
              class="h-24 rounded-md"
            />
          </div>

          <UEmpty
            v-else-if="agents.length === 0"
            icon="i-hugeicons-user-ai"
            title="No configured agents"
            description="When no agents are configured, Zeeq uses a generalized review agent for this repository."
            class="flex-1"
          />

          <div v-else class="min-h-0 flex-1 overflow-y-auto">
            <!-- Agent rows are intentionally dense; the detail pane carries full prompt/filter context. -->
            <button
              v-for="agent in agents"
              :key="agent.id"
              type="button"
              class="grid w-full cursor-pointer gap-1.5 border-b border-l-2 border-default px-4 py-3 text-left text-sm transition-colors sm:px-6"
              :class="
                selectedManagementItemId === agent.id
                  ? 'border-l-primary bg-primary/10'
                  : 'border-l-transparent hover:border-l-primary hover:bg-primary/5'
              "
              @click="selectAgent(agent)"
            >
              <div class="flex min-w-0 items-start justify-between gap-3">
                <div class="min-w-0">
                  <h3
                    class="truncate text-[13px] font-semibold text-highlighted"
                  >
                    {{ agent.displayName }}
                  </h3>
                  <p class="mt-0.5 truncate text-xs leading-4 text-muted">
                    {{ agent.reviewFacet }} · {{ agent.modelTier }} tier
                  </p>
                </div>
                <UBadge
                  :label="agent.enabled ? 'Enabled' : 'Disabled'"
                  :color="agent.enabled ? 'success' : 'neutral'"
                  size="sm"
                  variant="subtle"
                  class="rounded-full"
                />
              </div>
            </button>
          </div>
        </section>

        <section class="flex min-h-0 flex-1 flex-col overflow-hidden">
          <FileFiltersPanel
            v-if="selectedManagementItemId === managementFiltersItemId"
            :file-filter="repositoryConfiguration?.fileFilter ?? null"
            :saving="savingRepositoryConfiguration"
            :disabled="!canManageOrganization || !selectedRepositoryId"
            @save="saveRepositoryFilters"
          />

          <AgentConfigPanel
            v-else-if="selectedManagementItemId === managementConfigItemId"
            ref="agentConfigPanelRef"
            :agent="editingAgent"
            :saving="savingAgent"
            :disabled="!canManageOrganization || !selectedRepositoryId"
            :initial-form="copiedAgentForm"
            @cancel="cancelAgentConfig"
            @save="saveAgent"
            @open-source-library="openSourceLibrary"
          />

          <template v-else-if="selectedAgent">
            <div
              class="flex min-h-24 items-start justify-between gap-4 border-b border-default px-6 py-5"
            >
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h2 class="truncate text-xl font-semibold text-highlighted">
                    {{ selectedAgent.displayName }}
                  </h2>
                  <UBadge
                    :label="selectedAgent.reviewFacet"
                    color="neutral"
                    variant="outline"
                    class="rounded-full"
                  />
                  <UBadge
                    :label="selectedAgent.enabled ? 'Enabled' : 'Disabled'"
                    :color="selectedAgent.enabled ? 'success' : 'neutral'"
                    variant="subtle"
                    class="rounded-full"
                  />
                </div>
                <p class="mt-1 text-sm text-muted">
                  {{ selectedAgent.modelTier }} tier ·
                  {{ activationSummary(selectedAgent.activationConfiguration) }}
                </p>
              </div>

              <div class="flex shrink-0 items-center gap-2">
                <USelect
                  v-if="copyTargetRepositoryItems.length > 0"
                  v-model="copySelectModel"
                  :items="copyTargetRepositoryItems"
                  placeholder="Copy to repository"
                  color="neutral"
                  size="md"
                  :disabled="!canManageOrganization || savingAgent"
                  @update:model-value="openCopyConfirm"
                />
                <UFieldGroup>
                  <UButton
                    label="Edit"
                    icon="i-hugeicons-pencil-edit-02"
                    color="neutral"
                    variant="subtle"
                    :disabled="!canManageOrganization"
                    @click="openEditAgent(selectedAgent)"
                  />
                  <ZeeqPopConfirm
                    title="Delete reviewer agent?"
                    :body="`Delete ${selectedAgent.displayName} from this repository's reviewer agents?`"
                    confirm-label="Delete"
                    label="Delete"
                    icon="i-hugeicons-delete-02"
                    color="error"
                    variant="subtle"
                    :disabled="!canManageOrganization || savingAgent"
                    @confirm="deleteAgent(selectedAgent)"
                  />
                </UFieldGroup>
              </div>
            </div>

            <div class="flex min-h-0 flex-1 flex-col overflow-hidden px-6 py-5">
              <div class="flex min-h-0 flex-1 flex-col gap-5">
                <section class="grid gap-2">
                  <h3 class="text-sm font-semibold text-highlighted">
                    Activation filters
                  </h3>
                  <p class="text-sm text-muted">
                    Included files:
                    {{
                      selectedAgent.activationConfiguration.includedFiles.length
                    }}
                    · Excluded files:
                    {{
                      selectedAgent.activationConfiguration.excludedFiles.length
                    }}
                  </p>
                </section>

                <section class="flex min-h-0 flex-1 flex-col gap-2">
                  <div
                    class="agent-prompt-viewer min-h-0 flex-1 overflow-hidden"
                  >
                    <MdEditor
                      :model-value="selectedAgent.prompt"
                      language="en-US"
                      preview-theme="github"
                      :preview="false"
                      :preview-only="false"
                      :read-only="true"
                      :toolbars="readOnlyPromptToolbars"
                      :footers="[]"
                      :html-preview="false"
                      :no-upload-img="true"
                      :no-mermaid="true"
                      :no-katex="true"
                      :theme="editorTheme"
                    />
                  </div>
                </section>
              </div>
            </div>
          </template>

          <UEmpty
            v-else
            icon="i-hugeicons-user-ai"
            title="No configured agents"
            description="When no agents are configured, Zeeq uses a generalized review agent for this repository."
            class="flex-1"
          />
        </section>
      </div>
    </template>
  </div>

  <UModal v-model:open="copyConfirmOpen" title="Copy reviewer agent">
    <template #body>
      <p class="text-sm text-dimmed">
        Copy
        <span class="font-medium text-highlighted">{{
          selectedAgent?.displayName
        }}</span>
        from
        <span class="font-medium text-highlighted">{{ originRepoLabel }}</span>
        →
        <span class="font-medium text-highlighted">{{
          copyPendingTargetRepoLabel
        }}</span
        >? The agent will open in a configuration panel so you can review and
        save it.
      </p>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="cancelCopy"
        />
        <UButton
          label="Copy"
          color="primary"
          icon="i-hugeicons-copy-01"
          variant="subtle"
          :loading="savingAgent"
          @click="confirmCopyAgentTo"
        />
      </div>
    </template>
  </UModal>

  <!--
  Source library for seeding a new agent (create panel). Opened by the config
  panel's Copy button; the store owns template and per-repo agent loading and
  the emitted form is applied to the create draft.
  -->
  <AgentSourceLibrarySlideover
    v-model:open="sourceLibraryOpen"
    :templates="agentTemplates"
    :templates-loading="loadingAgentTemplates"
    :repository-options="sourceRepositoryItems"
    :repo-agents="sourceRepoAgents"
    :repo-agents-loading="loadingSourceRepoAgents"
    @request-templates="loadAgentTemplates"
    @request-repo-agents="loadSourceRepoAgents"
    @select="applySourceForm"
  />
</template>

<!--
  Manage Agents – Reviewer agent management view rendered under the Code Reviews
  parent layout. Route: /code-reviews/manage-agents (name: "ManageAgents").
  The left panel lists agents per repository; the right panel shows agent
  details, configuration, or the inlined AgentConfigPanel for create/edit.
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { useColorMode } from "@vueuse/core";
import { MdEditor, type ToolbarNames } from "md-editor-v3";
import "md-editor-v3/lib/style.css";
import { storeToRefs } from "pinia";
import type {
  CodeReviewFileFilterDto,
  CodeReviewerActivationConfigurationDto,
  CodeReviewerAgentDto,
  CodeReviewerAgentTemplateDto,
} from "@/api/generated";
import {
  agentToForm,
  managementConfigItemId,
  managementFiltersItemId,
  type CodeReviewerAgentForm,
  useCodeReviewStore,
} from "@/stores/code-review-store";
import { useAppStore } from "@/stores/app-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";

import AgentConfigPanel from "./AgentConfigPanel.vue";
import AgentSourceLibrarySlideover from "./AgentSourceLibrarySlideover.vue";
import FileFiltersPanel from "./FileFiltersPanel.vue";

const props = defineProps<{
  orgId?: string;
  agentId?: string;
}>();

const toast = useToast();
const colorMode = useColorMode();
const route = useRoute();
const router = useRouter();
const appStore = useAppStore();
const codeReviewStore = useCodeReviewStore();
const organizationSettingsStore = useOrganizationSettingsStore();
const { canManageOrganization } = storeToRefs(organizationSettingsStore);
const {
  selectedRepositoryId,
  repositoryConfiguration,
  agents,
  configuredRepositories,
  selectedManagementItemId,
  editingManagementAgent: editingAgent,
  copiedManagementAgentForm: copiedAgentForm,
  createManagementAgentRequestId,
  handledCreateManagementAgentRequestId,
  loadingRepositories,
  loadingAgents,
  savingAgent,
  savingRepositoryConfiguration,
  error: codeReviewError,
  activeOrganizationId,
  hasConfiguredRepositories,
} = storeToRefs(codeReviewStore);

const readOnlyPromptToolbars: ToolbarNames[] = ["preview", "previewOnly"];
const agentConfigPanelRef = ref<InstanceType<typeof AgentConfigPanel> | null>(
  null,
);
const editorTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

const selectedAgent = computed(
  () =>
    agents.value.find((agent) => agent.id === selectedManagementItemId.value) ??
    null,
);
/**
 * Agent source library slideover state. The library seeds a new agent from a
 * built-in template or an existing agent cloned from any configured repository.
 * Templates and per-repo agents load lazily on demand from the store.
 */
const sourceLibraryOpen = ref(false);
const agentTemplates = ref<CodeReviewerAgentTemplateDto[]>([]);
const loadingAgentTemplates = ref(false);
const sourceRepoAgents = ref<CodeReviewerAgentDto[]>([]);
const loadingSourceRepoAgents = ref(false);

/** Every configured repo in the org is a valid clone source, including the current one. */
const sourceRepositoryItems = computed(() =>
  configuredRepositories.value.map((repository) => ({
    label: repository.displayName,
    value: repository.id,
  })),
);

/** Opens the source library from the create panel's Copy button. */
function openSourceLibrary() {
  sourceLibraryOpen.value = true;
}

/** Loads the built-in reviewer templates into the library. */
async function loadAgentTemplates() {
  loadingAgentTemplates.value = true;

  try {
    agentTemplates.value = await codeReviewStore.listAgentTemplates();
  } catch (err: unknown) {
    showError("Could not load reviewer templates", err);
  } finally {
    loadingAgentTemplates.value = false;
  }
}

/** Loads reviewer agents for the chosen source repository into the library. */
async function loadSourceRepoAgents(repositoryId: string) {
  loadingSourceRepoAgents.value = true;
  sourceRepoAgents.value = [];

  try {
    sourceRepoAgents.value =
      await codeReviewStore.listRepositoryAgents(repositoryId);
  } catch (err: unknown) {
    showError("Could not load agents to copy", err);
  } finally {
    loadingSourceRepoAgents.value = false;
  }
}

/** Seeds the create panel's draft with a form chosen from the source library. */
function applySourceForm(form: CodeReviewerAgentForm) {
  copiedAgentForm.value = form;
}

const copyTargetRepositoryItems = computed(() =>
  configuredRepositories.value
    .filter((r) => r.id !== selectedRepositoryId.value)
    .map((r) => ({
      label: r.displayName,
      value: r.id,
    })),
);

const originRepoLabel = computed(() => {
  const repo = configuredRepositories.value.find(
    (r) => r.id === selectedRepositoryId.value,
  );
  return repo?.displayName ?? "current repository";
});

const copyConfirmOpen = ref(false);
const copyPendingTargetRepoId = ref<string | null>(null);
const copySelectModel = ref<string | undefined>(undefined);
let routeSelectionSyncId = 0;
const routeSelectionApplying = ref(false);

const copyPendingTargetRepoLabel = computed(() => {
  const repo = configuredRepositories.value.find(
    (r) => r.id === copyPendingTargetRepoId.value,
  );
  return repo?.displayName ?? "another repository";
});

/** Opens the confirmation dialog after the user picks a target repository. */
function openCopyConfirm(targetRepositoryId: string) {
  copyPendingTargetRepoId.value = targetRepositoryId;
  copyConfirmOpen.value = true;
}

/** Resets the copy selector and pending state when the user cancels. */
function cancelCopy() {
  copySelectModel.value = undefined;
  copyPendingTargetRepoId.value = null;
  copyConfirmOpen.value = false;
}

/** Confirms the copy, switches to the target repository, and opens the config
 *  panel pre-filled with the copied agent data. */
async function confirmCopyAgentTo() {
  copyConfirmOpen.value = false;

  await onCopyAgentTo(copyPendingTargetRepoId.value!);
  copySelectModel.value = undefined;
  copyPendingTargetRepoId.value = null;
}

/** Captures the selected agent's form, switches to the target repository, and
 *  opens the config panel pre-filled with the copied agent data. */
async function onCopyAgentTo(targetRepositoryId: string) {
  if (!selectedAgent.value || !canManageOrganization.value) return;

  copiedAgentForm.value = agentToForm(selectedAgent.value);
  editingAgent.value = null;

  await codeReviewStore.setSelectedRepository(targetRepositoryId);

  selectedManagementItemId.value = managementConfigItemId;
}

/** Resets copy state when the confirmation dialog is dismissed without confirming. */
watch(copyConfirmOpen, (isOpen) => {
  if (!isOpen && copyPendingTargetRepoId.value) {
    copySelectModel.value = undefined;
    copyPendingTargetRepoId.value = null;
  }
});

onMounted(async () => {
  await loadAgentManagement();
  await applyRouteAgentSelection();
  window.addEventListener("keydown", handleGlobalKeydown);
});

onBeforeUnmount(() => {
  window.removeEventListener("keydown", handleGlobalKeydown);
});

/** Active organization changes invalidate repository ids and management state. */
watch(activeOrganizationId, async () => {
  await loadAgentManagement();
  await applyRouteAgentSelection();
});

watch(selectedRepositoryId, () => {
  selectedManagementItemId.value = managementFiltersItemId;
  if (!routeSelectionApplying.value) {
    void replaceWithManageAgentsRoute();
  }
});

watch(agents, () => {
  if (
    selectedManagementItemId.value !== managementFiltersItemId &&
    selectedManagementItemId.value !== managementConfigItemId &&
    !selectedAgent.value
  ) {
    selectedManagementItemId.value = managementFiltersItemId;
  }
});

watch(selectedManagementItemId, (itemId) => {
  if (routeSelectionApplying.value) {
    return;
  }

  if (
    itemId === managementFiltersItemId ||
    (itemId === managementConfigItemId && !editingAgent.value)
  ) {
    void replaceWithManageAgentsRoute();
  }
});

/**
 * The "New agent" trigger lives in CodeReviews.vue's toolbar. The store emits a
 * request token so this view can open the source library even when create mode
 * is already selected and the user clicks the toolbar button again.
 */
watch(
  createManagementAgentRequestId,
  (requestId) => {
    if (
      requestId === 0 ||
      requestId <= handledCreateManagementAgentRequestId.value
    ) {
      return;
    }

    handledCreateManagementAgentRequestId.value = requestId;

    if (selectedManagementItemId.value !== managementConfigItemId) {
      return;
    }

    sourceRepoAgents.value = [];
    sourceLibraryOpen.value = true;
  },
  { immediate: true },
);

watch(
  () => [props.orgId, props.agentId] as const,
  async () => {
    await applyRouteAgentSelection();
  },
);

/** Loads repositories, selected repository config, and agent rows. */
async function loadAgentManagement() {
  try {
    await codeReviewStore.loadAgentManagement();
  } catch (err: unknown) {
    showError("Could not load reviewer agents", err);
  }
}

function selectAgent(agent: CodeReviewerAgentDto) {
  editingAgent.value = null;
  selectedManagementItemId.value = agent.id;
  void replaceWithAgentRoute(agent.id);
}

/** Persists repository-level filters. */
async function saveRepositoryFilters(fileFilter: CodeReviewFileFilterDto) {
  try {
    await codeReviewStore.saveRepositoryFileFilter(fileFilter);
    toast.add({
      title: "Repository filters saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not save repository filters", err);
  }
}

/** Opens the right-side config panel for one persisted agent row. */
function openEditAgent(agent: CodeReviewerAgentDto) {
  editingAgent.value = agent;
  selectedManagementItemId.value = managementConfigItemId;
}

function cancelAgentConfig() {
  copiedAgentForm.value = null;

  if (editingAgent.value) {
    selectedManagementItemId.value = editingAgent.value.id;
    editingAgent.value = null;
    return;
  }

  editingAgent.value = null;
  selectedManagementItemId.value = managementFiltersItemId;
}

/** Creates or updates an agent while keeping the config editor open. */
async function saveAgent(agentId: string | null, form: CodeReviewerAgentForm) {
  try {
    let savedAgent: CodeReviewerAgentDto;

    if (agentId) {
      savedAgent = await codeReviewStore.updateAgent(agentId, form);
    } else {
      savedAgent = await codeReviewStore.createAgent(form);
    }

    editingAgent.value = savedAgent;
    copiedAgentForm.value = null;
    selectedManagementItemId.value = managementConfigItemId;
    await replaceWithAgentRoute(savedAgent.id);
    toast.add({
      title: agentId ? "Reviewer agent saved" : "Reviewer agent created",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not save reviewer agent", err);
  }
}

/** Deletes an agent from the active list after the backend soft-disables it. */
async function deleteAgent(agent: CodeReviewerAgentDto) {
  try {
    await codeReviewStore.deleteAgent(agent.id);
    selectedManagementItemId.value = managementFiltersItemId;
    editingAgent.value = null;
    await replaceWithManageAgentsRoute();
    toast.add({
      title: "Reviewer agent deleted",
      description: agent.displayName,
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not delete reviewer agent", err);
  }
}

function activationSummary(
  configuration: CodeReviewerActivationConfigurationDto,
): string {
  const includeCount = configuration.includedFiles.length;
  const excludeCount = configuration.excludedFiles.length;

  if (includeCount === 0 && excludeCount === 0) {
    return "all filtered repository files";
  }

  return `${includeCount} include, ${excludeCount} exclude`;
}

function showError(title: string, err: unknown) {
  toast.add({
    title,
    description:
      err instanceof Error ? err.message : "Code review settings failed.",
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}

/** Resolves a canonical /manage-agents/:orgId/agents/:agentId link. */
async function applyRouteAgentSelection() {
  const syncId = ++routeSelectionSyncId;
  routeSelectionApplying.value = true;

  const isCurrentSync = () => syncId === routeSelectionSyncId;
  const finishCurrentSync = () => {
    if (isCurrentSync()) {
      routeSelectionApplying.value = false;
    }
  };

  // NOTE: Route-driven selection writes repository and item state in stages. Keep
  // watcher-triggered navigation suppressed until the latest route sync completes.
  if (!props.orgId || !props.agentId) {
    editingAgent.value = null;
    copiedAgentForm.value = null;
    selectedManagementItemId.value = managementFiltersItemId;
    finishCurrentSync();
    return;
  }

  if (activeOrganizationId.value !== props.orgId) {
    const canSwitchToRouteOrg = appStore.user?.organizations?.some(
      (org) => org.id === props.orgId,
    );
    if (!canSwitchToRouteOrg) {
      showError(
        "Could not load reviewer agent",
        new Error("Organization is not available."),
      );
      finishCurrentSync();
      return;
    }

    await appStore.switchOrganization(props.orgId);
    if (!isCurrentSync()) return;

    finishCurrentSync();
    return;
  }

  try {
    const linkedAgent = await codeReviewStore.getAgent(props.agentId);
    if (!isCurrentSync()) return;

    if (selectedRepositoryId.value !== linkedAgent.repositoryId) {
      await codeReviewStore.setSelectedRepository(linkedAgent.repositoryId);
      if (!isCurrentSync()) return;
    } else if (agents.value.length === 0) {
      await codeReviewStore.loadSelectedRepositoryManagement();
      if (!isCurrentSync()) return;
    }

    editingAgent.value = null;
    copiedAgentForm.value = null;
    selectedManagementItemId.value = linkedAgent.id;
  } catch (err: unknown) {
    if (!isCurrentSync()) return;

    showError("Could not load reviewer agent", err);
    await replaceWithManageAgentsRoute();
  } finally {
    finishCurrentSync();
  }
}

/** CMD+S / CTRL+S saves the active create/edit panel without opening a confirm flow. */
function handleGlobalKeydown(event: KeyboardEvent) {
  if (event.key.toLowerCase() !== "s" || !(event.metaKey || event.ctrlKey)) {
    return;
  }

  if (selectedManagementItemId.value !== managementConfigItemId) {
    return;
  }

  event.preventDefault();
  agentConfigPanelRef.value?.triggerSave();
}

async function replaceWithAgentRoute(agentId: string) {
  if (!activeOrganizationId.value) {
    return;
  }

  if (
    route.name === "ManageAgent" &&
    route.params.orgId === activeOrganizationId.value &&
    route.params.agentId === agentId
  ) {
    return;
  }

  await router.replace({
    name: "ManageAgent",
    params: { orgId: activeOrganizationId.value, agentId },
  });
}

async function replaceWithManageAgentsRoute() {
  if (route.name === "ManageAgents") {
    return;
  }

  await router.replace({ name: "ManageAgents" });
}
</script>

<style scoped>
/* MdEditor owns its root element; keep sizing scoped to the read-only prompt viewer. */
.agent-prompt-viewer :deep(.md-editor) {
  width: 100%;
  height: 100%;
  min-height: 0;
  box-sizing: border-box;
}
</style>
