<template>
  <USlideover
    :open="open"
    title="Organization"
    :ui="{ content: 'max-w-3xl' }"
    @update:open="updateOpen"
  >
    <template #body>
      <!-- Initial detail load skeleton keeps stale organizations from flashing during switches. -->
      <div v-if="loading && !organization" class="flex flex-col gap-3">
        <USkeleton v-for="index in 4" :key="index" class="h-14 rounded-md" />
      </div>

      <!-- Slideover detail shell delegates each tab to focused child components. -->
      <div v-else-if="organization" class="flex flex-col gap-4">
        <div
          class="flex flex-col gap-4 border-b border-default pb-4 sm:flex-row sm:items-start sm:justify-between"
        >
          <div class="flex min-w-0 items-start gap-3">
            <UAvatar
              :src="organization.iconUrl ?? undefined"
              :alt="organization.displayName"
              icon="i-hugeicons-building-03"
              size="lg"
            />
            <div class="min-w-0">
              <h2 class="truncate text-lg font-semibold text-highlighted">
                {{ organization.displayName }}
              </h2>
              <p class="truncate text-sm text-muted">
                {{ organization.slug ?? organization.id }}
              </p>
            </div>
          </div>

          <UUser
            :name="organization.creator.displayName"
            :description="
              organization.creator.email ?? organization.creator.userId
            "
            :avatar="{
              src: organization.creator.pictureUrl ?? undefined,
              alt: organization.creator.displayName,
              icon: 'i-hugeicons-user',
            }"
            size="lg"
            class="min-w-0 sm:max-w-72"
            :ui="{ name: 'truncate', description: 'truncate' }"
          />
        </div>

        <!-- Tabs are controlled by the route root so browser back/forward restores them. -->
        <UTabs
          :model-value="activeTab"
          :items="tabItems"
          :content="false"
          variant="link"
          @update:model-value="emitTab"
        />

        <SystemOrganizationGeneralTab
          v-if="activeTab === 'details'"
          :organization
          :saving
          @save="emits('save', $event)"
        />
        <SystemOrganizationMembersTab
          v-else-if="activeTab === 'members'"
          :members
          :page="membersPage"
          :page-size="membersPageSize"
          :total-count="membersTotalCount"
          :loading="loadingMembers"
          @page-change="emits('membersPageChange', $event)"
        />
        <SystemOrganizationLlmConfigTab
          v-else
          :configuration="organization.llmConfiguration"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type {
  SystemOrganizationDetailsResponse,
  SystemOrganizationMemberResponse,
} from "@/api/generated";
import type {
  SystemOrganizationTab,
  SystemOrganizationTier,
} from "@/stores/system-org-management-store";
import SystemOrganizationGeneralTab from "./SystemOrganizationGeneralTab.vue";
import SystemOrganizationMembersTab from "./SystemOrganizationMembersTab.vue";
import SystemOrganizationLlmConfigTab from "./SystemOrganizationLlmConfigTab.vue";

defineProps<{
  open: boolean;
  organization: SystemOrganizationDetailsResponse | null;
  members: SystemOrganizationMemberResponse[];
  membersPage: number;
  membersPageSize: number;
  membersTotalCount: number;
  loading: boolean;
  loadingMembers: boolean;
  saving: boolean;
  activeTab: SystemOrganizationTab;
}>();

/** Emits close/tab/save/member paging intents for the root store owner to handle. */
const emits = defineEmits<{
  close: [];
  tabChange: [tab: SystemOrganizationTab];
  save: [request: { active: boolean; tier: SystemOrganizationTier }];
  membersPageChange: [page: number];
}>();

/** Route-backed tab model rendered by Nuxt UI tabs. */
const tabItems = [
  { label: "General", value: "details", icon: "i-hugeicons-settings-02" },
  { label: "Members", value: "members", icon: "i-hugeicons-user-group" },
  {
    label: "LLM",
    value: "llm",
    icon: "i-hugeicons-artificial-intelligence-08",
  },
];

/** Converts Nuxt UI open changes into the root's query-state close action. */
function updateOpen(value: boolean) {
  if (!value) {
    emits("close");
  }
}

/** Narrows Nuxt UI tab values to the system organization tab union. */
function emitTab(value: string | number) {
  if (value === "details" || value === "members" || value === "llm") {
    emits("tabChange", value);
  }
}
</script>
