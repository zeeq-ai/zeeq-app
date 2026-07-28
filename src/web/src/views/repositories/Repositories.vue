<template>
  <ZeeqView
    id="repositories"
    title="Repositories"
    body-class="flex h-full min-h-0 flex-col gap-0 sm:gap-0 overflow-hidden p-0 sm:p-0"
  >
    <div v-if="error" class="grid gap-2 border-b border-default p-4 sm:px-6">
      <UAlert
        title="Repository settings unavailable"
        :description="error"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
      />
    </div>

    <div class="flex min-h-0 flex-1 overflow-hidden">
      <!-- Left: repository picker. -->
      <RepositoryList
        :repositories
        :selected-repository-id="selectedRepositoryId"
        :loading="loadingRepositories"
        @select="onSelectRepository"
      />

      <!-- Right: configuration for the selected repository. -->
      <RepositoryConfigPanel
        v-if="selectedRepository"
        :repository="selectedRepository"
        :organization-id="activeOrganizationId"
        :libraries
        :prompts
        :details="promptDetails"
        :loading-prompts="loadingPrompts"
        :loading-prompt-detail-id="loadingPromptDetailId"
        :saving-prompt-id="savingPromptId"
        :saving-libraries="savingLibraries"
        @save-libraries="onSaveLibraries"
        @expand-prompt="onExpandPrompt"
        @save-prompt="onSavePrompt"
      />

      <UEmpty
        v-else
        icon="i-hugeicons-github"
        title="Select a repository"
        description="Choose a repository to map libraries and customize the MCP prompts agents receive for it."
        class="flex-1"
      />
    </div>
  </ZeeqView>
</template>

<!--
  Repositories – repository configuration reachable by any organization member.
  Routes: /repositories (name: "Repositories") and /repositories/:repositoryId
  (name: "Repository") for deep links.

  Scope: library mapping and MCP prompt customization only. Connecting the GitHub
  App and enabling, pausing, or removing a repository remain owner/admin actions
  under Settings → GitHub, matching the backend authorization split.
-->
<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useRoute, useRouter } from "vue-router";
import ZeeqView from "@/components/ZeeqView.vue";
import { useRepositoryStore } from "@/stores/repository-store";

import RepositoryConfigPanel from "./RepositoryConfigPanel.vue";
import RepositoryList from "./RepositoryList.vue";

const props = defineProps<{
  repositoryId?: string;
}>();

const toast = useToast();
const route = useRoute();
const router = useRouter();
const repositoryStore = useRepositoryStore();

const {
  repositories,
  libraries,
  prompts,
  promptDetails,
  selectedRepositoryId,
  selectedRepository,
  loadingRepositories,
  loadingPrompts,
  loadingPromptDetailId,
  savingPromptId,
  savingLibraries,
  error,
  activeOrganizationId,
} = storeToRefs(repositoryStore);

onMounted(async () => {
  await load();
});

/** Switching organizations invalidates repository ids, so reload from scratch. */
watch(activeOrganizationId, async () => {
  await repositoryStore.selectRepository(null);
  await load();
});

/** Keeps the selection in sync when the route id changes (back/forward). */
watch(
  () => props.repositoryId,
  async (repositoryId) => {
    if (repositoryId && repositoryId !== selectedRepositoryId.value) {
      await selectAndLoad(repositoryId);
      return;
    }

    if (!repositoryId && selectedRepositoryId.value) {
      await repositoryStore.selectRepository(null);
    }
  },
);

/**
 * Loads repositories, then restores the routed selection. Falls back to the
 * first repository so the panel is never empty when one is available.
 */
async function load() {
  try {
    await repositoryStore.loadRepositories();
  } catch (err: unknown) {
    showError("Could not load repositories", err);
    return;
  }

  const routed = props.repositoryId
    ? repositories.value.find(
        (repository) => repository.id === props.repositoryId,
      )
    : null;
  const target = routed ?? repositories.value[0] ?? null;

  if (target) {
    await selectAndLoad(target.id);
  }
}

/** Selects a repository, loads its prompts, and reflects it in the URL. */
async function selectAndLoad(repositoryId: string) {
  try {
    await repositoryStore.selectRepository(repositoryId);
  } catch (err: unknown) {
    showError("Could not load repository prompts", err);
    return;
  }

  if (route.params.repositoryId !== repositoryId) {
    await router.replace({ name: "Repository", params: { repositoryId } });
  }
}

async function onSelectRepository(repositoryId: string) {
  await selectAndLoad(repositoryId);
}

/** Lazily loads a prompt's placeholders the first time its row is expanded. */
async function onExpandPrompt(documentId: string, libraryId: string) {
  try {
    await repositoryStore.loadPromptDetail(documentId, libraryId);
  } catch (err: unknown) {
    showError("Could not load prompt placeholders", err);
  }
}

async function onSavePrompt(
  documentId: string,
  libraryId: string,
  active: boolean,
  values: Record<string, string>,
) {
  try {
    await repositoryStore.savePrompt(documentId, libraryId, active, values);
    toast.add({
      title: active ? "Prompt customization saved" : "Prompt deactivated",
      description: active
        ? "Agents sending this repository's header will receive these values."
        : "Agents will receive the prompt's authored defaults. Saved values are kept.",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not save prompt customization", err);
  }
}

async function onSaveLibraries(libraryIds: string[]) {
  try {
    await repositoryStore.saveLibraries(libraryIds);
    toast.add({
      title: "Libraries saved",
      description: selectedRepository.value?.ownerQualifiedName,
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not save libraries", err);
  }
}

function showError(title: string, err: unknown) {
  toast.add({
    title,
    description:
      err instanceof Error ? err.message : "Repository settings failed.",
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
