<template>
  <div v-if="orgId && canManageOrganization" class="min-w-0 space-y-4">
    <UPageCard
      title="Teams"
      description="Group members in your organization."
      variant="naked"
      orientation="horizontal"
    >
      <UButton
        class="lg:ms-auto"
        icon="i-lucide-plus"
        label="New team"
        :to="{ name: 'SettingsNewTeam' }"
      />
    </UPageCard>

    <UPageCard variant="subtle" :ui="{ container: 'p-0 sm:p-0 gap-y-0' }">
      <div class="border-b border-default p-4">
        <UInput
          v-model="query"
          icon="i-lucide-search"
          aria-label="Search teams"
          placeholder="Search teams"
          :maxlength="200"
          class="w-full"
        />
      </div>
      <UAlert
        v-if="error"
        color="error"
        variant="subtle"
        :description="error"
        class="m-4"
      />
      <ul v-if="items.length" class="divide-y divide-default">
        <li v-for="team in items" :key="team.id">
          <ULink
            :to="{ name: 'SettingsTeam', params: { teamId: team.id } }"
            class="flex items-center justify-between gap-3 px-4 py-3 hover:bg-elevated/70"
          >
            <span class="min-w-0">
              <span class="block truncate font-medium text-highlighted">{{
                team.name
              }}</span>
              <span
                v-if="team.description"
                class="block truncate text-sm text-muted"
                >{{ team.description }}</span
              >
            </span>
            <span class="flex shrink-0 items-center gap-2 text-sm text-muted">
              {{ team.memberCount }}
              {{ Number(team.memberCount) === 1 ? "member" : "members" }}
              <UIcon name="i-lucide-chevron-right" aria-hidden="true" />
            </span>
          </ULink>
        </li>
      </ul>
      <p v-else-if="!error" class="p-4 text-sm text-muted">
        {{
          loading
            ? "Loading teams…"
            : query
              ? "No teams match your search."
              : "No teams yet."
        }}
      </p>
      <div v-if="nextCursor" class="border-t border-default p-3">
        <UButton variant="ghost" color="neutral" :loading @click="loadMore"
          >Load more teams</UButton
        >
      </div>
    </UPageCard>

    <TeamSlideover
      v-if="panelOpen"
      :key="orgId + ':' + (teamId ?? 'new')"
      :org-id
      :team-id
      @close="closePanel"
      @created="openCreatedTeam"
      @changed="reload"
    />
  </div>
  <UAlert
    v-else-if="orgId"
    color="neutral"
    description="Organization owners and admins can manage teams."
  />
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { storeToRefs } from "pinia";
import { refDebounced } from "@vueuse/core";
import { Teams } from "@/api/generated/clients/Teams";
import { useAppStore } from "@/stores/app-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import TeamSlideover from "./TeamSlideover.vue";
import { useTeamPages } from "./useTeamPages";

const router = useRouter();
const route = useRoute();
const { user } = storeToRefs(useAppStore());
const { canManageOrganization } = storeToRefs(useOrganizationSettingsStore());
const orgId = computed(() => user.value?.organizationId ?? null);
const query = ref("");
const search = refDebounced(query, 220);
const scope = computed(() =>
  orgId.value && canManageOrganization.value
    ? JSON.stringify([orgId.value, search.value])
    : null,
);
const { items, loading, error, nextCursor, reload, loadMore } = useTeamPages(
  scope,
  (after, signal) =>
    Teams.listTeams(
      orgId.value!,
      { q: search.value.trim() || undefined, after },
      { signal },
    ),
);
const teamId = computed(() =>
  typeof route.params.teamId === "string" ? route.params.teamId : undefined,
);
const panelOpen = computed(
  () => Boolean(teamId.value) || route.name === "SettingsNewTeam",
);

function closePanel() {
  void router.replace({ name: "SettingsTeams" });
}

function openCreatedTeam(id: string) {
  if (query.value) query.value = "";
  else void reload();
  void router.replace({ name: "SettingsTeam", params: { teamId: id } });
}
</script>
