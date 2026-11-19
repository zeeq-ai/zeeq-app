<template>
  <USlideover
    :open="true"
    :title="member.displayName"
    description="Organization team memberships"
    @update:open="!$event && emits('close')"
  >
    <template #body>
      <div class="space-y-4">
        <div class="flex items-center gap-3">
          <UAvatar
            :src="member.pictureUrl ?? undefined"
            :alt="member.displayName"
          />
          <p v-if="member.email" class="text-sm text-muted">
            {{ member.email }}
          </p>
        </div>
        <UAlert v-if="error" color="error" :description="error" />
        <ul v-if="items.length" class="divide-y divide-default">
          <li v-for="team in items" :key="team.id">
            <ULink
              :to="{ name: 'SettingsTeam', params: { teamId: team.id } }"
              class="flex items-center justify-between gap-3 py-3 text-highlighted hover:text-primary"
            >
              <span class="min-w-0 truncate">{{ team.name }}</span>
              <UIcon
                name="i-lucide-chevron-right"
                class="shrink-0"
                aria-hidden="true"
              />
            </ULink>
          </li>
        </ul>
        <p v-else-if="!error" class="text-sm text-muted">
          {{ loading ? "Loading teams…" : "This member has no teams yet." }}
        </p>
        <UButton
          v-if="nextCursor"
          variant="ghost"
          color="neutral"
          :loading
          @click="loadMore"
        >
          Load more teams
        </UButton>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { Teams } from "@/api/generated/clients/Teams";
import type { MemberResponse } from "@/api/generated/types/MemberResponse";
import { useTeamPages } from "./useTeamPages";

const props = defineProps<{ orgId: string; member: MemberResponse }>();
const emits = defineEmits<{ close: [] }>();

const scope = computed(() =>
  JSON.stringify([props.orgId, props.member.userId]),
);
const { items, loading, error, nextCursor, loadMore } = useTeamPages(
  scope,
  (after, signal) =>
    Teams.listMemberTeams(
      props.orgId,
      props.member.userId,
      { after },
      { signal },
    ),
);
</script>
