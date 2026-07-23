<template>
  <ZeeqView id="code-reviews" :body-class="bodyClass">
    <template #toolbar>
      <div class="flex w-full min-w-0 items-center gap-3">
        <UNavigationMenu
          :items="links"
          highlight
          class="-mx-1 min-w-0 flex-1"
        />

        <!--
        Manage Agents-only actions, consolidated here next to the repository
        picker instead of duplicating a second toolbar inside ManageAgents.vue.
        -->
        <UFieldGroup v-if="route.name === 'ManageAgents'" class="shrink-0">
          <UButton
            label="Global file filters"
            icon="i-hugeicons-filter"
            color="neutral"
            size="md"
            :variant="
              selectedManagementItemId === managementFiltersItemId
                ? 'subtle'
                : 'outline'
            "
            :disabled="!selectedRepositoryId"
            @click="codeReviewStore.selectManagementFilters()"
          />
          <UButton
            label="New agent"
            icon="i-hugeicons-plus-sign"
            color="neutral"
            size="md"
            :title="newAgentButtonTitle"
            :variant="
              selectedManagementItemId === managementConfigItemId
                ? 'subtle'
                : 'outline'
            "
            :disabled="!canCreateManagementAgent"
            @click="codeReviewStore.openCreateAgentPanel()"
          />
          <UButton
            label="Shared prompt"
            icon="i-hugeicons-sticky-note-01"
            color="neutral"
            size="md"
            variant="outline"
            :disabled="!selectedRepositoryId"
            @click="
              () => {
                sharedPromptFragmentOpen = true;
              }
            "
          />
        </UFieldGroup>

        <USelectMenu
          v-if="
            route.name !== 'CodeReviewSingle' &&
            route.name !== 'CodeReviewPullRequestSingle'
          "
          :model-value="repositorySelectorValue"
          :items="repositoryItems"
          value-key="value"
          :loading="repositorySelectorLoading"
          :placeholder="repositorySelectorPlaceholder"
          color="neutral"
          variant="outline"
          size="md"
          class="font-bold w-72"
          :disabled="repositorySelectorLoading || repositoryItems.length === 0"
          @update:model-value="changeRepository"
        />
      </div>
    </template>

    <RouterView />
  </ZeeqView>

  <SharedPromptFragmentSlideover
    v-if="route.name === 'ManageAgents'"
    v-model:open="sharedPromptFragmentOpen"
    :shared-prompt-fragment="
      repositoryConfiguration?.sharedPromptFragment ?? null
    "
    :saving="savingRepositoryConfiguration"
    :disabled="!canManageOrganization || !selectedRepositoryId"
    @save="saveSharedPromptFragment"
  />
</template>

<script setup lang="ts">
import type { NavigationMenuItem } from "@nuxt/ui";
import { computed, onMounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRoute } from "vue-router";
import {
  managementConfigItemId,
  managementFiltersItemId,
  useCodeReviewStore,
} from "@/stores/code-review-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";

import SharedPromptFragmentSlideover from "./manage-agents/SharedPromptFragmentSlideover.vue";

const route = useRoute();
const toast = useToast();
const codeReviewStore = useCodeReviewStore();
const organizationSettingsStore = useOrganizationSettingsStore();
const { canManageOrganization } = storeToRefs(organizationSettingsStore);
const {
  selectedRepositoryId,
  webhookEnabledRepositories,
  loadingRepositories,
  activeOrganizationId,
  selectedManagementItemId,
  repositoryConfiguration,
  savingRepositoryConfiguration,
  canCreateManagementAgent,
  newAgentButtonTitle,
} = storeToRefs(codeReviewStore);

const sharedPromptFragmentOpen = ref(false);
const repositoriesLoadAttempted = ref(false);

const bodyClass = computed(() =>
  route.name === "CodeReviewPullRequests" ||
  route.name === "ManageAgents" ||
  route.name === "ManageAgent" ||
  route.name === "CodeReviewSingle" ||
  route.name === "CodeReviewPullRequestSingle"
    ? "gap-0 sm:gap-0 overflow-hidden p-0 sm:p-0"
    : undefined,
);

const repositoryItems = computed(() =>
  webhookEnabledRepositories.value.map((repository) => ({
    label: repository.displayName,
    value: repository.id,
  })),
);
const selectedRepositoryResolved = computed(
  () =>
    !selectedRepositoryId.value ||
    repositoryItems.value.some(
      (item) => item.value === selectedRepositoryId.value,
    ),
);
const repositorySelectorPendingInitialLoad = computed(
  () =>
    !repositoriesLoadAttempted.value &&
    Boolean(selectedRepositoryId.value) &&
    !selectedRepositoryResolved.value,
);
const repositorySelectorLoading = computed(
  () => loadingRepositories.value || repositorySelectorPendingInitialLoad.value,
);
const repositorySelectorValue = computed(() =>
  repositorySelectorLoading.value || !selectedRepositoryResolved.value
    ? undefined
    : (selectedRepositoryId.value ?? undefined),
);
const repositorySelectorPlaceholder = computed(() =>
  repositorySelectorLoading.value
    ? "Loading..."
    : repositoryItems.value.length === 0
      ? "No repositories"
      : "Select repository...",
);

/** Ephemeral entry present only while viewing a single review; reflects the active route. */
const links = computed<NavigationMenuItem[][]>(() => {
  const base: NavigationMenuItem[] = [
    {
      label: "PR Code Reviews",
      to: "/code-reviews/pull-requests",
    },
    {
      label: "Manage Agents",
      to: "/code-reviews/manage-agents",
      active: route.name === "ManageAgents" || route.name === "ManageAgent",
    },
  ];

  if (route.name === "CodeReviewSingle") {
    base.push({
      label: "Code Review",
      to: route.fullPath,
      active: true,
    });
  }

  if (route.name === "CodeReviewPullRequestSingle") {
    base.push({
      label: "Pull Request",
      to: route.fullPath,
      active: true,
    });
  }

  return [base];
});

onMounted(async () => {
  await loadRepositories();
});

watch(activeOrganizationId, async () => {
  repositoriesLoadAttempted.value = false;
  await loadRepositories();
});

/** Loads toolbar repository options shared by both Code Reviews tabs. */
async function loadRepositories() {
  try {
    await codeReviewStore.loadConfiguredRepositories();
  } catch (err: unknown) {
    toast.add({
      title: "Could not load repositories",
      description:
        err instanceof Error ? err.message : "Repository lookup failed.",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  } finally {
    repositoriesLoadAttempted.value = true;
  }
}

/** Persists the repository-level shared prompt fragment from the toolbar slideover. */
async function saveSharedPromptFragment(sharedPromptFragment: string) {
  try {
    await codeReviewStore.saveSharedPromptFragment(sharedPromptFragment);
    sharedPromptFragmentOpen.value = false;
    toast.add({
      title: "Shared prompt fragment saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    toast.add({
      title: "Could not save shared prompt fragment",
      description:
        err instanceof Error ? err.message : "Code review settings failed.",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}

/** Changes the shared repository and reloads whichever tab is active. */
async function changeRepository(repositoryId: string) {
  try {
    if (route.name === "ManageAgents" || route.name === "ManageAgent") {
      await codeReviewStore.setSelectedRepository(repositoryId);
      return;
    }

    await codeReviewStore.setInboxRepositoryFilter(repositoryId);
  } catch (err: unknown) {
    toast.add({
      title: "Could not change repository",
      description:
        err instanceof Error ? err.message : "Repository selection failed.",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}
</script>
