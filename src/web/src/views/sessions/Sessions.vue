<!--
Sessions root view: agent-conversation inbox mirroring the PR Code Reviews
layout. Toolbar-free edge-to-edge body, two-column split of inbox (left) +
detail (right), with a mobile USlideover standing in for the desktop-only
detail column below the lg breakpoint. Only this component talks to the
Pinia store; children take props and emit.
-->
<template>
  <ZeeqView
    id="sessions"
    title="Sessions"
    body-class="flex h-full min-h-0 flex-col gap-0 sm:gap-0 overflow-hidden p-0 sm:p-0"
  >
    <UAlert
      v-if="error"
      title="Sessions unavailable"
      :description="error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
      class="m-4 sm:m-6"
    />

    <div class="flex min-h-0 flex-1 overflow-hidden">
      <SessionInboxList
        :conversations="conversations"
        :members="organizationMembers"
        :selected-conversation-id="selectedConversation?.id ?? null"
        :loading="loadingInbox"
        :loading-more="loadingMore"
        :has-next-page="nextCursor !== null"
        @select="onSelectConversation"
        @refresh="onRefresh"
        @load-more="onLoadMore"
      />

      <SessionDetailPanel
        :detail="selectedConversationDetail"
        :members="organizationMembers"
        :loading="loadingDetail"
        class="hidden lg:flex"
        @refresh="onDetailRefresh"
      />

      <USlideover
        v-if="isMobile"
        v-model:open="detailPanelOpen"
        :ui="{ content: 'max-w-xl' }"
      >
        <template #content>
          <SessionDetailPanel
            :detail="selectedConversationDetail"
            :members="organizationMembers"
            :loading="loadingDetail"
            show-close
            class="h-full"
            @close="closeDetailPanel"
            @refresh="onDetailRefresh"
          />
        </template>
      </USlideover>
    </div>
  </ZeeqView>
</template>

<script setup lang="ts">
import { computed, onMounted, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "vue-router";
import { breakpointsTailwind, useBreakpoints } from "@vueuse/core";
import { useSessionsStore } from "@/stores/sessions-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import type { AgentConversationListItemDto } from "@/api/generated";
import SessionInboxList from "./SessionInboxList.vue";
import SessionDetailPanel from "./SessionDetailPanel.vue";

const props = defineProps<{
  conversationId?: string;
}>();

const router = useRouter();
const toast = useToast();
const sessionsStore = useSessionsStore();
const organizationSettingsStore = useOrganizationSettingsStore();

const breakpoints = useBreakpoints(breakpointsTailwind);
const isMobile = breakpoints.smaller("lg");
const detailPanelOpen = computed({
  get() {
    return isMobile.value && selectedConversation.value !== null;
  },
  set(value: boolean) {
    if (!value) {
      closeDetailPanel();
    }
  },
});

const {
  conversations,
  nextCursor,
  loadingInbox,
  loadingMore,
  selectedConversation,
  selectedConversationDetail,
  loadingDetail,
  error,
  activeOrganizationId,
} = storeToRefs(sessionsStore);
const { members: organizationMembers } = storeToRefs(organizationSettingsStore);

onMounted(async () => {
  organizationSettingsStore.ensureMembersLoaded().catch(() => undefined);
  await refreshInbox();
});

/**
 * Reacts to the route's conversationId rather than only reading it once in
 * onMounted — Vue Router reuses this component for /sessions/:conversationId?
 * navigations (browser back/forward, or selecting a different conversation),
 * so an onMounted-only read would leave a stale conversation displayed.
 */
watch(
  () => props.conversationId,
  async (conversationId) => {
    if (!conversationId) {
      sessionsStore.clearSelection();
      return;
    }

    if (selectedConversation.value?.id === conversationId) {
      return;
    }

    try {
      await sessionsStore.loadConversationById(conversationId);
    } catch (err: unknown) {
      showError("Could not load conversation", err);
    }
  },
  { immediate: true },
);

/** Reload when the user switches organizations in the app shell. */
watch(activeOrganizationId, async () => {
  sessionsStore.clearSelection();
  await router.replace("/sessions");
  await refreshInbox();
});

async function refreshInbox() {
  try {
    await sessionsStore.loadInbox();
  } catch (err: unknown) {
    showError("Could not load Sessions inbox", err);
  }
}

/** Loads the selected row's detail and pushes its id into the URL so it's bookmarkable. */
async function onSelectConversation(conversation: AgentConversationListItemDto) {
  try {
    await sessionsStore.selectConversation(conversation);
    router.push(`/sessions/${conversation.id}`);
  } catch (err: unknown) {
    showError("Could not load conversation", err);
  }
}

/** Closes the mobile detail slideover and clears the route back to the bare inbox. */
function closeDetailPanel() {
  sessionsStore.clearSelection();
  router.replace("/sessions");
}

async function onRefresh() {
  await refreshInbox();
}

/**
 * Refreshes the currently open conversation's detail — used by both the manual
 * refresh button and the auto-refresh poll. Quiet on failure like the Code
 * Reviews inbox poller: a transient miss shouldn't interrupt the user with a
 * toast for what is, either way, a retryable no-op.
 */
async function onDetailRefresh() {
  if (!selectedConversation.value) {
    return;
  }

  try {
    await sessionsStore.loadConversationById(selectedConversation.value.id);
  } catch {
    // Intentionally quiet — see doc comment above.
  }
}

/** Fetches the next cursor page and appends it to the loaded inbox rows. */
async function onLoadMore() {
  try {
    await sessionsStore.loadNextPage();
  } catch (err: unknown) {
    showError("Could not load more conversations", err);
  }
}

function showError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : "Sessions request failed.",
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
