<template>
  <section class="flex min-h-0 flex-1 flex-col overflow-auto">
    <UAlert
      v-if="error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
      :title="error"
      class="m-4"
    />
    <div
      v-else-if="result"
      class="mx-auto w-full flex-1 lg:border-x lg:border-default"
      style="max-width: 1024px"
    >
      <div class="border-b border-default p-4 sm:px-6">
        <div class="flex flex-wrap items-center gap-2">
          <h2 class="text-xl font-semibold text-highlighted">
            {{ result.triage.findingSummary }}
          </h2>
          <UBadge
            :label="dispositionLabel"
            color="neutral"
            variant="outline"
            class="rounded-full"
          />
          <UBadge
            :label="result.triage.approvalState"
            :color="
              result.triage.approvalState === 'Proposed' ? 'info' : 'neutral'
            "
            variant="subtle"
            class="rounded-full"
          />
        </div>
        <p class="mt-1 text-sm text-muted">
          {{ result.triage.facet }} · {{ result.triage.semanticId }}
        </p>
      </div>
      <div class="grid gap-4 p-4 sm:px-6">
        <UAlert
          icon="i-hugeicons-information-circle"
          color="neutral"
          variant="subtle"
          title="Source review expired"
          description="The original review is no longer available. The finding and its triage record are shown below."
        />
        <div>
          <h3 class="text-sm font-medium text-highlighted">Reason</h3>
          <p class="mt-1 text-sm text-default whitespace-pre-wrap">
            {{ result.triage.currentReason }}
          </p>
        </div>
        <UAlert
          v-if="confirmError"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
          :title="confirmError"
        />
        <div v-if="result.triage.approvalState === 'Proposed'" class="flex">
          <UButton label="Confirm Proposal" :loading="busy" @click="confirm" />
        </div>
      </div>
    </div>
    <div v-else class="grid gap-3 p-4 sm:px-6">
      <USkeleton v-for="index in 3" :key="index" class="h-28 rounded-md" />
    </div>
  </section>
</template>
<script setup lang="ts">
import type { FindingTriageResponse } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { resolveFindingTriageLink } from "./resolveFindingTriageLink";
import { useCodeReviewStore } from "@/stores/code-review-store";
const route = useRoute();
const router = useRouter();
const app = useAppStore();
const store = useCodeReviewStore();
const result = ref<FindingTriageResponse | null>(null);
const error = ref("");
const confirmError = ref("");
const busy = ref(false);
const dispositionLabel = computed(() =>
  result.value?.triage.disposition === "FalsePositive"
    ? "False positive"
    : result.value?.triage.disposition === "Deferred"
      ? "Deferred"
      : "Open",
);
let generation = 0;
watch(
  () => [route.params.orgId, route.params.triageId],
  async ([orgId, triageId]) => {
    const current = ++generation;
    result.value = null;
    error.value = "";
    confirmError.value = "";
    busy.value = false;
    try {
      const resolution = await resolveFindingTriageLink(
        String(orgId),
        String(triageId),
        {
          activeOrgId: app.user?.organizationId,
          organizationIds: app.user?.organizations?.map((org) => org.id) ?? [],
          switchOrganization: app.switchOrganization,
          load: store.loadFindingTriage,
          origin: window.location.origin,
          base: import.meta.env.BASE_URL,
        },
      );
      if (current !== generation) return;
      if (resolution.kind === "redirect") await router.replace(resolution.path);
      if (resolution.kind === "expired") result.value = resolution.result;
    } catch (e) {
      if (current === generation)
        error.value = e instanceof Error ? e.message : "Could not load triage.";
    }
  },
  { immediate: true, flush: "sync" },
);
onUnmounted(() => {
  generation++;
});
async function confirm() {
  if (!result.value || busy.value) return;
  const current = generation;
  confirmError.value = "";
  const triage = result.value.triage;
  busy.value = true;
  try {
    const updated = await store.confirmFindingTriage(triage.id, {
      expectedDisposition: triage.disposition,
      reason: null,
    });
    if (current === generation && result.value?.triage.id === triage.id)
      result.value.triage = updated;
  } catch (e) {
    if (current === generation)
      confirmError.value =
        e instanceof Error ? e.message : "Could not confirm triage.";
  } finally {
    if (current === generation) busy.value = false;
  }
}
</script>
