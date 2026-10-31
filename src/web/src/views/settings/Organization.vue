<template>
  <div>
    <UPageCard
      title="Organization"
      description="Manage the active organization's display details."
      variant="naked"
      orientation="horizontal"
      class="mb-4"
    >
      <div class="flex w-fit gap-2 lg:ms-auto">
        <UButton
          v-if="canManageOrganization"
          label="History"
          icon="i-hugeicons-time-02"
          color="neutral"
          variant="outline"
          @click="openOrganizationHistory"
        />
        <UButton
          label="New organization"
          icon="i-hugeicons-plus-sign"
          color="neutral"
          variant="ghost"
          :disabled="saving"
          @click="
            () => {
              newOrganizationOpen = true;
            }
          "
        />
        <UButton
          label="Save changes"
          icon="i-hugeicons-floppy-disk"
          color="neutral"
          :loading="saving"
          :disabled="!canManageOrganization"
          @click="formRef?.submit()"
        />
      </div>
    </UPageCard>

    <UAlert
      v-if="!canManageOrganization"
      icon="i-hugeicons-information-circle"
      color="neutral"
      variant="subtle"
      title="View only"
      description="Only organization owners and admins can edit these settings."
      class="mb-4"
    />

    <OrganizationForm
      ref="formRef"
      :organization
      :can-manage="canManageOrganization"
      :saving
      @save="saveOrganization"
      @error="showError"
    />

    <SameDomainOnboardingSettings
      class="mt-4"
      :organization
      :can-manage="canManageOrganization"
      :saving
      @update="saveSameDomainOnboarding"
    />

    <USlideover
      v-model:open="newOrganizationOpen"
      title="New organization"
      description="Create an organization where you are the owner."
    >
      <template #body>
        <OrganizationForm
          v-if="newOrganizationOpen"
          ref="newOrganizationFormRef"
          :organization="null"
          can-manage
          condensed
          :saving
          @save="createOrganization"
          @error="showCreateError"
        />
        <div class="mt-4 flex justify-end">
          <UButton
            label="Create organization"
            icon="i-hugeicons-plus-sign"
            color="neutral"
            :loading="saving"
            @click="newOrganizationFormRef?.submit()"
          />
        </div>
      </template>
    </USlideover>

    <AuditHistoryFeed
      v-model:open="historyOpen"
      scope="organization"
      :scope-label="historyScopeLabel"
      :title="historyTitle"
      :description="historyDescription"
      :range="historyRange"
      :entries="historyEntries"
      :total-count="null"
      :has-more="historyHasMore"
      :loading="historyLoading"
      :error="historyError"
      :type-filter="historyTypeFilter"
      :area-filter="historyAreaFilter"
      :actor-filter="historyActorFilter"
      :actor-filter-label="historyActorFilterLabel"
      :coverage-label="historyCoverage"
      :capture-note="historyCaptureNote"
      :expanded-entry-id="historyExpandedEntryId"
      :detail="historyDetail"
      :detail-loading="historyDetailLoading"
      :detail-error="historyDetailError"
      :expanded-operation-id="historyExpandedOperationId"
      :operation-states="historyOperationStates"
      @retry="auditHistoryStore.retry"
      @retry-detail="auditHistoryStore.retryDetail"
      @toggle-detail="auditHistoryStore.toggleDetail"
      @toggle-operation="auditHistoryStore.toggleOperation"
      @load-operation="auditHistoryStore.loadOperation"
      @type-filter-change="auditHistoryStore.setTypeFilter"
      @area-filter-change="auditHistoryStore.setAreaFilter"
      @actor-filter-change="auditHistoryStore.setActorFilter"
      @clear-filters="auditHistoryStore.clearFilters"
      @range-change="auditHistoryStore.setRange"
      @load-older="auditHistoryStore.loadOlder"
      @search-all-time="auditHistoryStore.searchAllTime"
    />
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { storeToRefs } from "pinia";
import OrganizationForm from "./components/OrganizationForm.vue";
import SameDomainOnboardingSettings from "./components/SameDomainOnboardingSettings.vue";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import { useAuditHistoryStore } from "@/stores/audit-history-store";
import AuditHistoryFeed from "@/components/audit/AuditHistoryFeed.vue";
import { Libraries } from "@/api/generated";
import type { CreateOrganizationRequest } from "@/api/generated/types/CreateOrganizationRequest";
import type { SameDomainOnboardingRole } from "@/stores/organization-settings-store";

const toast = useToast();
const settingsStore = useOrganizationSettingsStore();
const { organization, saving, canManageOrganization } =
  storeToRefs(settingsStore);
const auditHistoryStore = useAuditHistoryStore();
const {
  range: historyRange,
  entries: historyEntries,
  hasMore: historyHasMore,
  loading: historyLoading,
  error: historyError,
  typeFilter: historyTypeFilter,
  areaFilter: historyAreaFilter,
  actorFilter: historyActorFilter,
  actorFilterLabel: historyActorFilterLabel,
  coverage: historyCoverage,
  expandedEntryId: historyExpandedEntryId,
  detail: historyDetail,
  detailLoading: historyDetailLoading,
  detailError: historyDetailError,
  expandedOperationId: historyExpandedOperationId,
  operationStates: historyOperationStates,
} = storeToRefs(auditHistoryStore);
const historyOpen = ref(false);
const currentOrganization = computed(() =>
  organization.value?.id === settingsStore.currentOrganizationId
    ? organization.value
    : null,
);
const historyScopeLabel = computed(
  () => currentOrganization.value?.displayName ?? "Organization",
);
const historyTitle = computed(() =>
  currentOrganization.value?.displayName
    ? `History · ${currentOrganization.value.displayName}`
    : "Organization history",
);
const historyDescription = computed(() =>
  currentOrganization.value?.displayName
    ? `Recorded changes for ${currentOrganization.value.displayName}.`
    : "Recorded changes in this organization.",
);
const historyCaptureNote = computed(() =>
  historyCoverage.value
    ? {
        title: "Capture coverage",
        description: historyCoverage.value,
      }
    : null,
);
const formRef = ref<InstanceType<typeof OrganizationForm> | null>(null);
const newOrganizationFormRef = ref<InstanceType<
  typeof OrganizationForm
> | null>(null);
const newOrganizationOpen = ref(false);
let libraryLookupRequestId = 0;

async function openOrganizationHistory() {
  const organizationId = settingsStore.currentOrganizationId;
  if (!canManageOrganization.value || !organizationId) return;
  const requestId = ++libraryLookupRequestId;
  if (organization.value?.id !== organizationId) {
    try {
      await settingsStore.loadCurrentOrganization();
    } catch {
      // The history title falls back until the current organization loads.
    }
  }
  if (settingsStore.currentOrganizationId !== organizationId) return;
  const baseContext = { scope: "organization" as const, organizationId };
  auditHistoryStore.setContext({ ...baseContext, libraryLabels: {} });
  historyOpen.value = true;
  void auditHistoryStore.loadInitial();
  try {
    const libraries = await Libraries.listLibraries(organizationId);
    if (
      requestId !== libraryLookupRequestId ||
      settingsStore.currentOrganizationId !== organizationId ||
      !historyOpen.value
    ) {
      return;
    }
    auditHistoryStore.setContext({
      ...baseContext,
      libraryLabels: Object.fromEntries(
        libraries.map(({ id, name }) => [id, name]),
      ),
    });
  } catch {
    // History stays available with stable library ID labels if this lookup fails.
  }
}

/**
 * Loads organization details when entering the settings route directly.
 */
onMounted(async () => {
  try {
    await settingsStore.loadCurrentOrganization();
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not load organization.",
    );
  }
});

watch(
  () => settingsStore.currentOrganizationId,
  (organizationId, previousOrganizationId) => {
    if (organizationId === previousOrganizationId || !historyOpen.value) return;
    libraryLookupRequestId++;
    historyOpen.value = false;
    if (organizationId) {
      auditHistoryStore.setContext({
        scope: "organization",
        organizationId,
      });
    }
  },
);

/** Saves organization metadata through the feature store. */
async function saveOrganization(request: CreateOrganizationRequest) {
  try {
    await settingsStore.updateOrganization(request);
    toast.add({
      title: "Organization saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not save organization.",
    );
  }
}

/** Creates a new organization and switches the shell to it. */
async function createOrganization(request: CreateOrganizationRequest) {
  try {
    await settingsStore.createOrganization(request);
    newOrganizationOpen.value = false;
    toast.add({
      title: "Organization created",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showCreateError(
      err instanceof Error ? err.message : "Could not create organization.",
    );
  }
}

/** Saves same-domain onboarding settings through the feature store. */
async function saveSameDomainOnboarding(request: {
  enabled: boolean;
  defaultRole?: SameDomainOnboardingRole | null;
}) {
  try {
    await settingsStore.updateSameDomainOnboarding(request);
    toast.add({
      title: "Onboarding settings saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showToastError(
      "Onboarding settings failed",
      err instanceof Error
        ? err.message
        : "Could not save onboarding settings.",
    );
  }
}

/** Shows form and API errors in the shared Nuxt UI toast surface. */
function showError(message: string) {
  showToastError("Organization update failed", message);
}

/** Shows create-specific form and API errors in the shared toast surface. */
function showCreateError(message: string) {
  showToastError("Organization create failed", message);
}

/** Shows form and API errors in the shared Nuxt UI toast surface. */
function showToastError(title: string, message: string) {
  toast.add({
    title,
    description: message,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
