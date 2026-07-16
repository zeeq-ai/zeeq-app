import "./style.css";

import { createApp } from "vue";
import ui from "@nuxt/ui/vue-plugin";
import { useRegisterSW } from "virtual:pwa-register/vue";
import App from "./App.vue";
import router from "./router";
import { createHead } from "@unhead/vue/client";

const head = createHead();
const app = createApp(App);
const pinia = createPinia();

const { needRefresh, updateServiceWorker } = useRegisterSW({
  immediate: true,
  onNeedRefresh() {
    needRefresh.value = true;
  },
  onOfflineReady() {},
});

app.provide("pwaNeedRefresh", needRefresh);
app.provide("pwaUpdateServiceWorker", updateServiceWorker);

app.use(pinia);
app.use(head);
app.use(router);
app.use(ui);

app.mount("#app");
