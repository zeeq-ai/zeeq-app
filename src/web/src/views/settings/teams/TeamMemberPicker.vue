<template>
  <section class="space-y-3">
    <h3 class="text-sm font-semibold text-highlighted">Add members</h3>
    <label :for="filterId" class="sr-only"
      >Search active organization members</label
    >
    <UListbox
      v-model="selection"
      v-model:search-term="query"
      :items
      value-key="value"
      multiple
      ignore-filter
      :filter="{
        placeholder: 'Search by name or email',
        id: filterId,
        maxlength: 200,
      }"
      :disabled
      aria-label="Active organization members"
      :ui="{ content: 'max-h-52 overflow-y-auto' }"
    >
      <template #empty>
        <p v-if="loading" class="p-2 text-sm text-muted">Searching members…</p>
        <p v-else-if="!error" class="p-2 text-sm text-muted">
          {{
            query
              ? "No active members match your search."
              : "No members available to add."
          }}
        </p>
      </template>
    </UListbox>
    <UAlert v-if="error" color="error" :description="error" />
    <UButton
      v-if="nextCursor"
      size="sm"
      variant="ghost"
      color="neutral"
      :loading
      @click="loadMore"
    >
      Load more people
    </UButton>
    <div v-if="selected.length" class="flex flex-wrap gap-1">
      <UButton
        v-for="id in selected"
        :key="id"
        type="button"
        size="xs"
        color="neutral"
        variant="soft"
        trailing-icon="i-lucide-x"
        :disabled
        :aria-label="'Deselect ' + selectedPeople.get(id)"
        @click="deselect(id)"
      >
        {{ selectedPeople.get(id) }}
      </UButton>
    </div>
    <p v-if="selected.length === 100" class="text-sm text-muted">
      Save these 100 members before adding more.
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, useId } from "vue";
import { refDebounced } from "@vueuse/core";
import { Teams } from "@/api/generated/clients/Teams";
import { useTeamPages } from "./useTeamPages";

const props = defineProps<{
  orgId: string;
  teamId?: string;
  disabled: boolean;
}>();
const selected = defineModel<string[]>({ required: true });
const selectedPeople = new Map<string, string>();
const filterId = useId();
const query = ref("");
const search = refDebounced(query, 220);
const scope = computed(() =>
  JSON.stringify([props.orgId, props.teamId, search.value]),
);
const {
  items: people,
  nextCursor,
  loading,
  error,
  loadMore,
  reload,
} = useTeamPages(scope, (after, signal) => {
  const params = { q: search.value.trim() || undefined, after };
  return props.teamId
    ? Teams.listTeamCandidates(props.orgId, props.teamId, params, { signal })
    : Teams.listNewTeamCandidates(props.orgId, params, { signal });
});

const items = computed(() =>
  people.value.map((person) => ({
    label: person.displayName,
    description: person.email ?? undefined,
    value: person.userId,
    avatar: { src: person.pictureUrl ?? undefined, alt: person.displayName },
    disabled:
      selected.value.length >= 100 && !selected.value.includes(person.userId),
  })),
);

const visibleIds = computed(
  () => new Set(people.value.map((person) => person.userId)),
);
const selection = computed({
  get: () => selected.value.filter((id) => visibleIds.value.has(id)),
  set: (ids: string[]) => {
    // Listbox only owns the visible candidates. Keep selections from other searches.
    const offPage = selected.value.filter((id) => !visibleIds.value.has(id));
    const next = [...offPage, ...ids];
    if (next.length > 100) return;

    for (const person of people.value) {
      if (ids.includes(person.userId)) {
        selectedPeople.set(person.userId, person.displayName);
      } else {
        selectedPeople.delete(person.userId);
      }
    }
    selected.value = next;
  },
});

function deselect(id: string) {
  selectedPeople.delete(id);
  selected.value = selected.value.filter((selectedId) => selectedId !== id);
}

defineExpose({ reload });
</script>
