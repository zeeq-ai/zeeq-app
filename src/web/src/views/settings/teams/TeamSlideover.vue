<template>
  <USlideover
    :open="true"
    :title="creating ? 'New team' : (team?.name ?? 'Team')"
    :description="
      creating
        ? 'Group active members in your organization.'
        : (team?.description ?? undefined)
    "
    :dismissible="!saving"
    :ui="{ content: 'w-full sm:max-w-lg' }"
    @update:open="!$event && emits('close')"
  >
    <template #body>
      <div class="space-y-5">
        <UAlert
          v-if="error || teamError || roster.error.value"
          color="error"
          :description="error || teamError || roster.error.value || undefined"
        />
        <form
          v-if="creating"
          id="create-team-form"
          class="space-y-4"
          @submit.prevent="save"
        >
          <UFormField label="Team name" required>
            <UInput
              v-model="name"
              required
              :maxlength="200"
              :disabled="saving"
              class="w-full"
            />
          </UFormField>
          <UFormField label="Description">
            <UTextarea
              v-model="description"
              :maxlength="500"
              :disabled="saving"
              class="w-full"
            />
          </UFormField>
        </form>

        <section v-if="team" class="space-y-2">
          <h3 class="text-sm font-semibold text-highlighted">
            Members ({{ team.memberCount }})
          </h3>
          <UInput
            v-model="rosterQuery"
            aria-label="Search team members"
            placeholder="Search team members"
            icon="i-lucide-search"
            :maxlength="200"
            class="w-full"
          />
          <ul v-if="roster.items.value.length" class="divide-y divide-default">
            <li
              v-for="person in roster.items.value"
              :key="person.userId"
              class="flex items-center gap-3 py-2"
            >
              <UAvatar
                :src="person.pictureUrl ?? undefined"
                :alt="person.displayName"
                size="sm"
              />
              <div class="min-w-0 flex-1 text-sm">
                <p class="truncate text-highlighted">
                  {{ person.displayName }}
                </p>
                <p v-if="person.email" class="truncate text-muted">
                  {{ person.email }}
                </p>
              </div>
              <ZeeqPopConfirm
                title="Remove from team"
                :body="`Remove ${person.displayName} from ${team.name}?`"
                confirm-label="Remove from team"
                icon="i-lucide-user-minus"
                color="error"
                variant="ghost"
                :disabled="saving"
                @confirm="remove(person.userId)"
              />
            </li>
          </ul>
          <p v-else-if="roster.loading.value" class="text-sm text-muted">
            Loading members…
          </p>
          <p v-else-if="!roster.error.value" class="text-sm text-muted">
            {{
              rosterQuery
                ? "No team members match your search."
                : "No members yet."
            }}
          </p>
          <UButton
            v-if="roster.nextCursor.value"
            variant="ghost"
            color="neutral"
            :loading="roster.loading.value"
            @click="roster.loadMore"
          >
            Load more members
          </UButton>
        </section>
        <p v-else-if="!creating && !teamError" class="text-sm text-muted">
          Loading team…
        </p>

        <TeamMemberPicker
          v-if="creating || team"
          ref="picker"
          v-model="selected"
          :org-id
          :team-id
          :disabled="saving"
        />
      </div>
    </template>
    <template #footer>
      <div
        v-if="creating || team"
        class="flex w-full items-center justify-between gap-3"
      >
        <span class="text-sm text-muted" aria-live="polite"
          >{{ selected.length }} selected</span
        >
        <UButton
          v-if="creating"
          type="submit"
          form="create-team-form"
          :disabled="!name.trim()"
          :loading="saving"
          label="Create team"
        />
        <UButton
          v-else
          :disabled="!selected.length"
          :loading="saving"
          :label="
            `Add ${selected.length || ''} ${selected.length === 1 ? 'member' : 'members'}`.replace(
              '  ',
              ' ',
            )
          "
          @click="save"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed, onScopeDispose, onMounted, ref, useTemplateRef } from "vue";
import { refDebounced } from "@vueuse/core";
import { Teams } from "@/api/generated/clients/Teams";
import type { TeamSummary } from "@/api/generated/types/TeamSummary";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import TeamMemberPicker from "./TeamMemberPicker.vue";
import { useTeamPages } from "./useTeamPages";

const props = defineProps<{ orgId: string; teamId?: string }>();
const emits = defineEmits<{
  close: [];
  created: [teamId: string];
  changed: [];
}>();
const toast = useToast();
const creating = computed(() => !props.teamId);
const name = ref("");
const description = ref("");
const selected = ref<string[]>([]);
const saving = ref(false);
const error = ref("");
const teamError = ref("");
const team = ref<TeamSummary>();
const picker = useTemplateRef("picker");
const rosterQuery = ref("");
const rosterSearch = refDebounced(rosterQuery, 220);
const rosterScope = computed(() =>
  props.teamId
    ? {
        orgId: props.orgId,
        teamId: props.teamId,
        q: rosterSearch.value.trim() || undefined,
      }
    : null,
);
const roster = useTeamPages(
  rosterScope,
  ({ orgId, teamId, q }, after, signal) =>
    Teams.listTeamMembers(orgId, teamId, { q, after }, { signal }),
);
const requests = new AbortController();
let detailRequest: AbortController | undefined;
onScopeDispose(() => {
  requests.abort();
  detailRequest?.abort();
});

async function loadTeam() {
  if (!props.teamId) return;
  detailRequest?.abort();
  const request = new AbortController();
  detailRequest = request;
  teamError.value = "";
  try {
    const detail = await Teams.getTeam(props.orgId, props.teamId, {
      signal: request.signal,
    });
    if (!request.signal.aborted) team.value = detail;
  } catch (cause) {
    if (!request.signal.aborted) teamError.value = message(cause);
  }
}

// Teams keys this panel by organization and team, including the creation route.
onMounted(loadTeam);

async function save() {
  if (
    saving.value ||
    (creating.value ? !name.value.trim() : !selected.value.length)
  )
    return;
  saving.value = true;
  error.value = "";
  try {
    if (!props.teamId) {
      const result = await Teams.createTeam(
        props.orgId,
        {
          name: name.value.trim(),
          description: description.value.trim() || null,
          userIds: selected.value,
        },
        { signal: requests.signal },
      );
      toast.add({ title: "Team created" });
      emits("created", result.teamId);
      return;
    }

    const count = selected.value.length;
    await Teams.updateTeamMembers(
      props.orgId,
      props.teamId,
      { add: selected.value, remove: null },
      { signal: requests.signal },
    );
    selected.value = [];
    toast.add({ title: count === 1 ? "Member added" : "Members added" });
    refreshTeam();
  } catch (cause) {
    if (!requests.signal.aborted) error.value = message(cause);
  } finally {
    saving.value = false;
  }
}

async function remove(userId: string) {
  if (!props.teamId || saving.value) return;
  saving.value = true;
  error.value = "";
  try {
    await Teams.updateTeamMembers(
      props.orgId,
      props.teamId,
      { add: null, remove: [userId] },
      { signal: requests.signal },
    );
    toast.add({ title: "Member removed from team" });
    refreshTeam();
  } catch (cause) {
    if (!requests.signal.aborted) error.value = message(cause);
  } finally {
    saving.value = false;
  }
}

function refreshTeam() {
  void loadTeam();
  void roster.reload();
  void picker.value?.reload();
  emits("changed");
}

function message(cause: unknown) {
  return cause instanceof Error
    ? cause.message
    : "The request could not be completed.";
}
</script>
