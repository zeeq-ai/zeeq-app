<template>
  <div class="flex flex-col items-center justify-center gap-4 p-4 min-h-screen">
    <UPageCard
      v-if="!showInactiveOrgNotice"
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

        <USeparator label="Support and early access" class="my-4" />

        <UButton
          block
          size="lg"
          color="neutral"
          variant="subtle"
          to="mailto:hello@zeeq.ai"
          :avatar="{
            src: avatarSrc,
            loading: 'lazy',
          }"
          external
        >
          Contact / Request activation key
        </UButton>

        <span class="text-dimmed text-xs">
          New organizations will require a one time activation key after sign
          up.
        </span>

        <USeparator label="Notices" class="my-4" />

        <span class="text-dimmed text-xs"
          >By signing in, you agree to our
          <a
            href="https://zeeq.ai/docs/policy/terms-of-service"
            target="_blank"
            class="underline font-bold"
            >Terms of Service</a
          >
          and
          <a
            href="https://zeeq.ai/docs/policy/privacy-policy"
            target="_blank"
            class="underline font-bold"
            >Privacy Policy</a
          >.</span
        >
      </div>
    </UPageCard>

    <!-- NOTE: Activation-key capability discovery is deferred. Until the login
    page can know feature availability up front, the exchange call's 404 fallback
    supplies the terse unavailable notice for disabled deployments. -->
    <template v-if="showInactiveOrgNotice">
      <UPageCard
        class="w-full max-w-md"
        title="Activate organization"
        description="Enter the activation key provided by a system administrator."
        icon="i-hugeicons-shield-key"
        :ui="{ footer: 'w-full self-stretch' }"
      >
        <UAlert
          v-if="activationError"
          title="Activation failed"
          :description="activationError"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
          class="mb-4"
        />

        <UForm
          :state="activationForm"
          class="flex flex-col gap-4"
          @submit="activateOrganization"
        >
          <UFormField label="Activation key" name="key">
            <UInput
              v-model="activationForm.key"
              class="w-full"
              autocomplete="one-time-code"
              placeholder="64-character key"
            />
          </UFormField>

          <UButton
            type="submit"
            block
            class="justify-center"
            icon="i-hugeicons-shield-key"
            color="neutral"
            variant="subtle"
            :loading="exchanging"
            :disabled="normalizedActivationKey.length !== 64"
          >
            Activate
          </UButton>

          <USeparator label="Support and early access" class="my-4" />

          <UButton
            block
            size="lg"
            color="neutral"
            variant="subtle"
            to="mailto:hello@zeeq.ai"
            :avatar="{
              src: avatarSrc,
              loading: 'lazy',
            }"
            external
          >
            Contact / Request activation key
          </UButton>

          <span class="text-dimmed text-xs">
            New organizations require a one time activation key after sign up.
          </span>
        </UForm>

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
              Use a different account
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
import { useOrganizationActivationStore } from "@/stores/organization-activation-store";
import { storeToRefs } from "pinia";

const toast = useToast();
const router = useRouter();
const route = useRoute();
const appStore = useAppStore();
const activationStore = useOrganizationActivationStore();
const { exchanging, error: activationError } = storeToRefs(activationStore);
const loggingOut = ref(false);
const avatarSrc = `${import.meta.env.BASE_URL}favicon-32x32.png`;
const activationForm = reactive({ key: "" });
const normalizedActivationKey = computed(() =>
  activationForm.key.trim().toLowerCase(),
);

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
  microsoft: "i-hugeicons-microsoft",
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
    return;
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

/** Activates the current inactive organization using the existing signed-in session. */
async function activateOrganization() {
  if (normalizedActivationKey.value.length !== 64) {
    return;
  }

  try {
    await activationStore.exchange(normalizedActivationKey.value);
    await appStore.fetchUser({ force: true });
    toast.add({
      title: "Organization activated",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
    await router.push("/");
  } catch {
    // Store error is rendered above.
  }
}
</script>
