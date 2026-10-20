<template>
  <!--
  Reporting-period selector shared by the Home toolbar, Library metrics header and the
  findings drill-down. A USelect lists the rolling presets, then (after a separator)
  up to three recent custom UTC ranges and a "Custom date range…" action. Picking a
  preset or recent range applies immediately. The date editor is a UPopover owned by a
  separate calendar button next to the select (its real trigger and anchor), so it is
  never nested in — or dismissed by — the select menu. The menu's Custom action opens
  that same popover, but only after the menu's close-focus handoff (see `onMenuCloseAutoFocus`).
  Stores stay out of this component: the period and history arrive as props and
  committed changes leave as `select` events for the root view to apply.
  -->
  <div class="inline-flex max-w-full min-w-0 items-start gap-2">
    <USelect
      :model-value="selectedValue"
      :items="items"
      icon="i-hugeicons-clock-01"
      :aria-label="accessibleName"
      :ui="selectUi"
      :content="menuContent"
      class="min-w-48 flex-1"
      @update:model-value="onSelectValue"
    >
      <!-- Always the applied period, even when it is not (or no longer) a menu entry. -->
      <template #default>{{ periodLabel }}</template>
    </USelect>

    <!-- Direct entry to the editor; also the focus target for every way of closing it. -->
    <UPopover
      v-model:open="editorOpen"
      :content="popoverContent"
      :ui="{ content: 'max-w-none' }"
    >
      <UButton
        ref="calendarButton"
        icon="i-hugeicons-calendar-03"
        color="neutral"
        variant="subtle"
        size="lg"
        square
        aria-label="Choose a custom date range (UTC)"
        class="shrink-0"
      />

      <template #content>
        <!-- Mounted only while open, so every open starts from the applied period and Cancel discards. -->
        <div @keydown.capture="onEditorKeydownCapture">
          <MetricDateRangeEditor
            :period="period"
            :today-utc="todayUtc"
            @apply="onApply"
            @cancel="closeEditorAndRestoreFocus"
          />
        </div>
      </template>
    </UPopover>
  </div>
</template>

<script setup lang="ts">
import type { SelectItem } from "@nuxt/ui";
import {
  absolutePeriod,
  formatMetricDateRange,
  isMetricWindowToken,
  metricPeriodLabel,
  metricWindowItems,
  relativePeriod,
  type MetricDateRange,
  type MetricPeriod,
} from "@/stores/metric-period";
import MetricDateRangeEditor from "./MetricDateRangeEditor.vue";

const props = defineProps<{
  /** Applied period (preset or custom UTC range). */
  period: MetricPeriod;
  /** Up to three recent custom ranges, newest first. */
  recentRanges: MetricDateRange[];
  /** UTC "today" (yyyy-MM-dd), used as the calendar's max date. */
  todayUtc: string;
}>();

const emits = defineEmits<{
  /** A preset, recent range or newly applied custom range was chosen. */
  select: [period: MetricPeriod];
  /** The date editor opened/closed; the root pauses auto-refresh while true. */
  editingChange: [editing: boolean];
}>();

/** Sentinel for the non-persistent "Custom date range…" menu action. */
const customValue = "__custom__";

const editorOpen = ref(false);
const calendarButton = useTemplateRef<{ $el: HTMLElement }>("calendarButton");

/** Set when "Custom…" is picked; consumed by the menu's close-focus handoff. */
let openEditorAfterMenuClose = false;
let restoreCalendarFocusAfterClose = false;

/** Escape is the one dismissal path where the non-modal popover left focus on BODY. */
const popoverContent = {
  align: "end" as const,
  side: "bottom" as const,
  onEscapeKeyDown: () => {
    restoreCalendarFocusAfterClose = true;
  },
  onCloseAutoFocus: (event: Event) => {
    if (!restoreCalendarFocusAfterClose) {
      return;
    }
    restoreCalendarFocusAfterClose = false;
    event.preventDefault();
    restoreCalendarButtonFocus();
  },
};

const rangeValue = (range: MetricDateRange) =>
  `range:${range.startDate}:${range.endDate}`;

const periodValue = computed(() =>
  props.period.mode === "relative"
    ? props.period.window
    : rangeValue(props.period),
);

/** Selected item value, kept in sync with the applied period (never the custom action). */
const selectedValue = computed(() => periodValue.value);

const periodLabel = computed(() => metricPeriodLabel(props.period));

const accessibleName = computed(() => `Time range: ${periodLabel.value}`);

/** Recent-range menu entries with their full UTC labels (precomputed for the template). */
const recentItems = computed(() =>
  props.recentRanges.map((range) => ({
    label: formatMetricDateRange(range),
    value: rangeValue(range),
    range,
  })),
);

const items = computed<SelectItem[]>(() => {
  const entries: SelectItem[] = [...metricWindowItems, { type: "separator" }];
  if (recentItems.value.length > 0) {
    entries.push(
      { type: "label", label: "Recent date ranges" },
      ...recentItems.value.map(({ label, value }) => ({ label, value })),
      { type: "separator" },
    );
  }
  entries.push({
    label: "Custom date range…",
    value: customValue,
    icon: "i-hugeicons-calendar-03",
  });
  return entries;
});

/**
 * Closed trigger wraps instead of truncating so both endpoints, the year and UTC stay
 * readable; the open menu grows to its widest entry (but never past the viewport) and
 * scrolls, with recent-range rows allowed to wrap.
 */
const selectUi = {
  base: "h-auto min-h-9",
  value: "overflow-visible whitespace-normal text-start text-clip",
  placeholder: "overflow-visible whitespace-normal text-start text-clip",
  content:
    "w-auto min-w-(--reka-select-trigger-width) max-w-[calc(100vw-1rem)] max-h-[min(24rem,var(--reka-select-content-available-height,24rem))]",
  itemLabel: "whitespace-normal",
};

/**
 * Menu close-focus handoff. Radix/Reka returns focus to the select trigger when the menu
 * closes; opening the popover before that happens gets it dismissed as a focus-outside.
 * For the Custom action we cancel that default restoration, move focus to the calendar
 * button (the popover's own trigger, so the popover treats it as "inside") and open the
 * editor there. Every other close keeps the default focus return to the select.
 */
const menuContent = {
  onCloseAutoFocus: (event: Event) => {
    if (!openEditorAfterMenuClose) {
      return;
    }
    openEditorAfterMenuClose = false;
    event.preventDefault();
    calendarButton.value?.$el.focus();
    editorOpen.value = true;
  },
};

watch(editorOpen, (open) => emits("editingChange", open));

function onSelectValue(value: unknown) {
  if (typeof value !== "string") {
    return;
  }
  if (value === customValue) {
    openEditorAfterMenuClose = true;
    return;
  }
  if (isMetricWindowToken(value)) {
    emits("select", relativePeriod(value));
    return;
  }
  const recent = recentItems.value.find((item) => item.value === value);
  if (recent) {
    emits("select", absolutePeriod(recent.range));
  }
}

function onApply(range: MetricDateRange) {
  emits("select", absolutePeriod(range));
  closeEditorAndRestoreFocus();
}

/** Segmented date inputs can consume Escape before the non-modal Popover sees it. */
function onEditorKeydownCapture(event: KeyboardEvent) {
  if (event.key !== "Escape") {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  closeEditorAndRestoreFocus();
}

/** Cancel and Apply are explicit close actions, so return focus to the editor trigger. */
function closeEditorAndRestoreFocus() {
  restoreCalendarFocusAfterClose = true;
  editorOpen.value = false;
}

/** Reassert after Vue unmounts the focused editor control and finishes the close update. */
function restoreCalendarButtonFocus() {
  const focusCalendarButton = () => calendarButton.value?.$el.focus();
  focusCalendarButton();
  void nextTick(focusCalendarButton);
}
</script>
