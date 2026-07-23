<template>
  <div class="flex flex-col items-center justify-center gap-4 p-4 min-h-screen">
    <UPageCard
      class="w-full max-w-md"
      title="Choose sign-in provider"
      description="Connect or create your account to continue."
      icon="i-hugeicons-user"
    >
      <div class="flex flex-col gap-3">
        <!-- Temporary disable
        <UAlert
          variant="outline"
          description="New org activation currently requires admin action."
          icon="i-hugeicons-information-square"
        >
        </UAlert>
        -->

        <UButton
          v-for="provider in providers"
          :key="provider.name"
          block
          size="lg"
          color="neutral"
          variant="subtle"
          :icon="provider.icon"
          :disabled="!provider.enabled"
          class="justify-center"
          @click="startLogin(provider)"
        >
          {{ provider.displayName }}
        </UButton>

        <p
          v-if="providers.length === 0"
          class="text-sm text-dimmed text-center"
        >
          No sign-in providers are available.
        </p>

        <USeparator label="Reach out" class="my-4" />

        <UButton
          block
          size="lg"
          color="neutral"
          variant="subtle"
          to="https://www.linkedin.com/in/charlescchen/"
          :avatar="{
            src: avatarSrc,
            loading: 'lazy',
          }"
          external
        >
          Linkedin
        </UButton>
      </div>
    </UPageCard>

    <template v-if="showInactiveOrgNotice">
      <UPageCard
        class="w-full max-w-md"
        title="Organization requires activation to continue"
        description="The organization associated with this account is not active yet. Activation by the account team is required before you can continue using organization-scoped features. Log out if you want to try a different account."
        icon="i-hugeicons-shield-02"
        :ui="{ footer: 'w-full self-stretch' }"
      >
        <template #footer>
          <div class="w-full">
            <UButton
              block
              class="w-full justify-center"
              icon="i-hugeicons-logout-03"
              color="neutral"
              variant="subtle"
              :loading="loggingOut"
              @click="logout"
            >
              OK
            </UButton>
          </div>
        </template>
      </UPageCard>
    </template>
  </div>
</template>

<script setup lang="ts">
import { useRouter, useRoute } from "vue-router";
import { isActivationReturnUrl } from "@/router/return-url";
import { useAppStore } from "@/stores/app-store";

const toast = useToast();
const router = useRouter();
const route = useRoute();
const appStore = useAppStore();
const loggingOut = ref(false);
const avatarSrc = `${import.meta.env.BASE_URL}favicon-32x32.png`;

interface LoginProvider {
  name: string;
  displayName: string;
  icon: string;
  enabled: boolean;
}

interface ProviderSummary {
  name: string;
  displayName: string;
  enabled: boolean;
}

const providerIcons: Record<string, string> = {
  github: "i-hugeicons-github",
  google: "i-hugeicons-google",
  mock: "i-hugeicons-shield-02",
};

const providers = ref<LoginProvider[]>([]);

const showInactiveOrgNotice = computed(
  () => route.query.inactiveOrg === "true",
);

/**
 * Redirects to /auth/login/{provider} with the current ?returnUrl=.
 */
function startLogin(provider: LoginProvider) {
  const returnUrl = sanitizeReturnUrl(
    new URLSearchParams(window.location.search).get("returnUrl"),
  );
  window.location.href = `/auth/login/${encodeURIComponent(provider.name)}?returnUrl=${encodeURIComponent(returnUrl)}`;
}

/**
 * Shows error toast if backend redirected back with ?error=.
 */
onMounted(() => {
  void loadProviders();

  const errorMsg = route.query.error as string | undefined;
  if (errorMsg) {
    toast.add({ title: "Login failed", description: errorMsg, color: "error" });
    router.replace({ query: {} });
    return;
  }

  if (isActivationReturnUrl(route.query.returnUrl)) {
    const query = { ...route.query };
    delete query.returnUrl;
    router.replace({ query });
  }
});

async function loadProviders() {
  const response = await fetch("/auth/providers", { credentials: "include" });
  if (!response.ok) {
    providers.value = [];
    return;
  }

  const providerSummaries = (await response.json()) as ProviderSummary[];
  providers.value = providerSummaries
    .filter((provider) => provider.enabled)
    .filter((provider) => provider.name !== "mock" || import.meta.env.DEV)
    .map((provider) => ({
      name: provider.name,
      displayName: provider.displayName,
      enabled: provider.enabled,
      icon: providerIcons[provider.name] ?? "i-hugeicons-user",
    }));
}

/** Normalizes stale login targets before handing them to the OAuth backend. */
function sanitizeReturnUrl(returnUrl: string | null): string {
  if (!returnUrl || isActivationReturnUrl(returnUrl)) {
    return "/";
  }

  return returnUrl;
}

/** Ends the current session so the user can retry with another account. */
async function logout() {
  loggingOut.value = true;

  try {
    await appStore.logout();
    await router.push("/login");
  } finally {
    loggingOut.value = false;
  }
}
</script>
