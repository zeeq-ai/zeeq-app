<template>
  <div class="flex flex-col gap-4">
    <UPageCard
      title="GitHub"
      description="Manage the GitHub App connection for the active organization."
      variant="naked"
    />

    <UAlert
      v-if="installationLinked"
      title="GitHub App connected"
      description="The installation callback completed and Zeeq saved the connection."
      icon="i-hugeicons-tick-02"
      color="success"
      variant="subtle"
    />

    <UAlert
      v-if="!canManageOrganization"
      title="View only"
      description="Only organization owners and admins can connect the GitHub App."
      icon="i-hugeicons-information-circle"
      color="neutral"
      variant="subtle"
    />

    <GitHubInstallationPanel
      :can-manage="canManageOrganization"
      :installation-linked="installationConnectedForPanel"
      @connect="startInstallation"
    />

    <GitHubRepositoryMappingsPanel
      :rows="repositoryRows"
      :can-manage="canManageOrganization"
      :loading="loadingRepositories"
      :saving-repository-id="savingRepositoryId"
      :error="githubSettingsError"
      @refresh="loadRepositories"
      @enable="enableRepository"
      @pause="pauseRepository"
      @resume="resumeRepository"
      @manage-libraries="openLibraryMappings"
    />

    <RepositoryLibraryMappingsSlideover
      v-model:open="libraryMappingsOpen"
      v-model:check-run-config="checkRunConfig"
      :repository="libraryMappingsTarget"
      :libraries="libraries"
      :submit-handler="onSubmitRepositorySettings"
      :remove-handler="onRemoveRepository"
    />
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { computed, onMounted } from "vue";
import { useRoute } from "vue-router";

import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";
import type { CodeReviewCheckRunConfigurationDto } from "@/api/generated/types/CodeReviewCheckRunConfigurationDto";
import { useGitHubSettingsStore } from "@/stores/github-settings-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import { useLibraryStore } from "@/stores/library-store";
import { useCodeReviewStore } from "@/stores/code-review-store";

import GitHubInstallationPanel from "./GitHubInstallationPanel.vue";
import GitHubRepositoryMappingsPanel from "./GitHubRepositoryMappingsPanel.vue";
import RepositoryLibraryMappingsSlideover from "./RepositoryLibraryMappingsSlideover.vue";

const githubInstallationLinkUrl = "/api/v1/integrations/github/install/link";

const route = useRoute();
const toast = useToast();
const githubSettingsStore = useGitHubSettingsStore();
const organizationSettingsStore = useOrganizationSettingsStore();
const libraryStore = useLibraryStore();
const codeReviewStore = useCodeReviewStore();
const { canManageOrganization } = storeToRefs(organizationSettingsStore);
const { repositoryConfiguration } = storeToRefs(codeReviewStore);
const {
  repositoryRows,
  configuredRepositories,
  installationConnected,
  loadingRepositories,
  savingRepositoryId,
  error: githubSettingsError,
} = storeToRefs(githubSettingsStore);
const { libraries } = storeToRefs(libraryStore);

/**
 * Backend callback returns here after it validates state, verifies the GitHub
 * installation, and persists the organization mapping.
 */
const installationLinked = computed(
  () => route.query.installationLinked === "true",
);

/** A normal repository load proves the GitHub App installation is linked. */
const installationConnectedForPanel = computed(
  () => installationLinked.value || installationConnected.value === true,
);

// ── Library mappings slideover ──────────────────────────────────────────

const libraryMappingsOpen = ref(false);
const libraryMappingsTarget = ref<GitHubConfiguredRepository | null>(null);
const checkRunConfig = ref<CodeReviewCheckRunConfigurationDto | null>(null);

/** Opens the library mapping slideover for a configured repository. */
async function openLibraryMappings(repositoryId: string) {
  const repository =
    configuredRepositories.value.find((r) => r.id === repositoryId) ?? null;
  libraryMappingsTarget.value = repository;
  checkRunConfig.value = null;

  if (repository) {
    try {
      await codeReviewStore.setSelectedRepository(repositoryId);
      checkRunConfig.value = repositoryConfiguration.value?.checkRun ?? null;
    } catch (err: unknown) {
      showRepositoryError("Could not load repository settings", err);
      return;
    }
  }

  libraryMappingsOpen.value = true;
}

/** Saves library mappings and check-run configuration for the target repository. */
async function onSubmitRepositorySettings(
  libraryIds: string[],
  runConfig: CodeReviewCheckRunConfigurationDto | null,
) {
  const repository = libraryMappingsTarget.value;
  if (!repository) return;

  try {
    await githubSettingsStore.updateRepositoryLibraries(repository, libraryIds);

    // NOTE: Spreading repositoryConfiguration.value is safe here because
    // openLibraryMappings only sets libraryMappingsOpen=true after a successful
    // setSelectedRepository call, so the store value is always fully hydrated
    // for the active repository by the time save is invoked.
    await codeReviewStore.saveRepositoryConfiguration({
      ...repositoryConfiguration.value,
      fileFilter: repositoryConfiguration.value?.fileFilter ?? {
        includedFiles: [],
        excludedFiles: [],
      },
      checkRun: runConfig,
    });

    toast.add({
      title: "Settings saved",
      description: repository.displayName,
      color: "success",
      icon: "i-hugeicons-tick-02",
    });
    libraryMappingsOpen.value = false;
  } catch (err: unknown) {
    showRepositoryError("Could not save repository settings", err);
  }
}

onMounted(async () => {
  await loadRepositories();
  // Load libraries in the background so the mapping slideover has data.
  libraryStore.loadLibraries().catch(() => undefined);

  if (installationLinked.value) {
    toast.add({
      title: "GitHub App connected",
      description: "Zeeq can now receive GitHub events for this organization.",
      color: "success",
      icon: "i-hugeicons-tick-02",
    });
  }
});

/**
 * This route intentionally uses browser navigation instead of the generated API
 * client because the backend answers with an external GitHub redirect.
 */
function startInstallation() {
  if (!canManageOrganization.value) {
    toast.add({
      title: "Permission required",
      description: "Ask an organization owner or admin to connect GitHub.",
      color: "warning",
      icon: "i-hugeicons-information-circle",
    });
    return;
  }

  window.location.assign(githubInstallationLinkUrl);
}

/** Loads repository mappings and reports setup problems without blocking the page. */
async function loadRepositories() {
  try {
    await githubSettingsStore.loadRepositories();
  } catch (err: unknown) {
    toast.add({
      title: "Could not load repositories",
      description:
        err instanceof Error
          ? err.message
          : "GitHub repositories are unavailable.",
      color: "error",
      icon: "i-hugeicons-alert-02",
    });
  }
}

/** Enables repository webhook ingress for the active organization. */
async function enableRepository(ownerQualifiedName: string) {
  if (!canManageOrganization.value) {
    showPermissionToast();
    return;
  }

  try {
    await githubSettingsStore.enableRepository(ownerQualifiedName);
    toast.add({
      title: "Repository enabled",
      description: `${ownerQualifiedName} can now create Zeeq review work.`,
      color: "success",
      icon: "i-hugeicons-tick-02",
    });
  } catch (err: unknown) {
    showRepositoryError("Could not enable repository", err);
  }
}

/** Pauses webhook ingress while keeping the mapping visible and reversible. */
async function pauseRepository(repositoryId: string) {
  const repository = findConfiguredRepository(repositoryId);
  if (!repository) {
    return;
  }

  await setRepositoryEnabled(repository, false, "Repository paused");
}

/** Resumes a paused repository mapping. */
async function resumeRepository(repositoryId: string) {
  const repository = findConfiguredRepository(repositoryId);
  if (!repository) {
    return;
  }

  await setRepositoryEnabled(repository, true, "Repository resumed");
}

/** Removes the active mapping from the repository config slideover. */
async function onRemoveRepository() {
  const repository = libraryMappingsTarget.value;
  if (!repository) return;

  try {
    await githubSettingsStore.removeRepository(repository.id);
    toast.add({
      title: "Repository removed",
      description: "Zeeq will ignore new webhooks for that repository.",
      color: "success",
      icon: "i-hugeicons-tick-02",
    });
    libraryMappingsOpen.value = false;
  } catch (err: unknown) {
    showRepositoryError("Could not remove repository", err);
  }
}

/** Applies the reversible enabled flag for one configured repository mapping. */
async function setRepositoryEnabled(
  repository: GitHubConfiguredRepository,
  enabled: boolean,
  successTitle: string,
) {
  if (!canManageOrganization.value) {
    showPermissionToast();
    return;
  }

  try {
    await githubSettingsStore.setRepositoryEnabled(repository, enabled);
    toast.add({
      title: successTitle,
      description: repository.ownerQualifiedName,
      color: "success",
      icon: "i-hugeicons-tick-02",
    });
  } catch (err: unknown) {
    showRepositoryError("Could not update repository", err);
  }
}

/** Finds the configured mapping needed for update requests. */
function findConfiguredRepository(
  repositoryId: string,
): GitHubConfiguredRepository | null {
  return (
    configuredRepositories.value.find(
      (repository) => repository.id === repositoryId,
    ) ?? null
  );
}

/** Shows the same permission feedback used by the installation action. */
function showPermissionToast() {
  toast.add({
    title: "Permission required",
    description: "Ask an organization owner or admin to manage GitHub.",
    color: "warning",
    icon: "i-hugeicons-information-circle",
  });
}

/** Shows repository-management API failures in a consistent toast shape. */
function showRepositoryError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : "GitHub update failed.",
    color: "error",
    icon: "i-hugeicons-alert-02",
  });
}
</script>
