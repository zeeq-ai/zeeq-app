<template>
  <div class="p-6">
    <UAlert v-if="error" color="error" :title="error" />
    <UCard v-else-if="result" class="max-w-2xl mx-auto">
      <template #header>Source review expired</template>
      <div class="grid gap-3">
        <p>{{ result.triage.findingSummary }}</p>
        <p>{{ result.triage.facet }} · {{ result.triage.semanticId }}</p>
        <p>
          Disposition:
          {{
            result.triage.disposition === "FalsePositive"
              ? "False positive"
              : result.triage.disposition === "Deferred"
                ? "Deferred"
                : "Open"
          }}
        </p>
        <p>Status: {{ result.triage.approvalState }}</p>
        <p>{{ result.triage.currentReason }}</p>
        <UAlert v-if="confirmError" color="error" :title="confirmError" />
        <UButton
          v-if="result.triage.approvalState === 'Proposed'"
          label="Confirm Proposal"
          :loading="busy"
          @click="confirm"
        />
      </div>
    </UCard>
    <USkeleton v-else class="h-32" />
  </div>
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
