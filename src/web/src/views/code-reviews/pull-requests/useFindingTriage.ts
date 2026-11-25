import type {
  CodeReviewFindingDto,
  CodeReviewRecordDto,
  CodeReviewFindingDisposition,
} from "@/api/generated";
import { useCodeReviewStore } from "@/stores/code-review-store";

export type TriageAction = {
  review: CodeReviewRecordDto;
  finding: CodeReviewFindingDto;
  action: "save" | "clear" | "confirm";
  disposition?: CodeReviewFindingDisposition;
  reason?: string;
  requestId?: string;
};

/** Owns request state at the host so repeated clicks cannot race. */
export function useFindingTriage() {
  const store = useCodeReviewStore();
  const toast = useToast();
  const triageBusyKeys = ref<string[]>([]);
  const triageSuccessVersions = ref<Record<string, string>>({});
  async function handleTriage(payload: TriageAction) {
    const key = `${payload.review.id}:${payload.finding.occurrenceKey}`;
    if (triageBusyKeys.value.includes(key)) return;
    if (payload.action === "confirm" && !payload.finding.triage) return;
    triageBusyKeys.value = [...triageBusyKeys.value, key];
    try {
      if (payload.action === "confirm" && payload.finding.triage) {
        await store.confirmFindingTriage(payload.finding.triage.id, {
          expectedDisposition: payload.finding.triage.disposition,
          reason: null,
        });
      } else if (payload.action === "clear") {
        await store.clearFindingTriage(
          payload.review,
          payload.finding.findingRef,
        );
      } else if (payload.action === "save") {
        await store.saveFindingTriage(payload.review, {
          findingReference: payload.finding.findingRef,
          disposition: payload.disposition ?? null,
          reason: payload.reason ?? null,
        });
      }
      if (payload.action !== "confirm") {
        triageSuccessVersions.value = {
          ...triageSuccessVersions.value,
          [key]: payload.requestId ?? "",
        };
      }
    } catch (error) {
      toast.add({
        title: "Could not update triage",
        description:
          error instanceof Error ? error.message : "Please try again.",
        color: "error",
      });
    } finally {
      triageBusyKeys.value = triageBusyKeys.value.filter(
        (item) => item !== key,
      );
    }
  }
  return { triageBusyKeys, triageSuccessVersions, handleTriage };
}
