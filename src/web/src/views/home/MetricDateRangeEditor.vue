<template>
  <!--
  Draft editor for a custom UTC date range, rendered inside the selector's
  popover. Everything here is a local draft: nothing is fetched, stored or
  recorded in history until Apply emits a complete, valid range. The popover
  unmounts this component on close, so Cancel / Escape / outside dismissal
  discard the draft and the next open re-seeds it from the applied period.
  Layout: labeled Start/End inputs, then a range calendar (one month on narrow
  screens, two when wide), then guidance + actions. The whole panel scrolls
  vertically so it always fits the viewport.
  -->
  <div
    role="group"
    aria-label="Custom date range (UTC)"
    class="flex max-h-[calc(100dvh-5rem)] w-[min(38rem,calc(100vw-1.5rem))] flex-col gap-3 overflow-y-auto p-3"
  >
    <!-- Typed entry: independent of the calendar; reversed values show an error, never a silent swap. -->
    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
      <UFormField label="Start date (UTC)" name="startDate">
        <UInputDate
          v-model="startField"
          :max-value="todayDate"
          :color="fieldColor"
          class="w-full"
        />
      </UFormField>

      <UFormField label="End date (UTC)" name="endDate">
        <UInputDate
          v-model="endField"
          :max-value="todayDate"
          :color="fieldColor"
          class="w-full"
        />
      </UFormField>
    </div>

    <!--
    Range calendar. `maximumDays` is deliberately not used (Reka previews the wrong span
    with it); instead dates beyond 29 days from a pending anchor are disabled, and the
    final range is re-validated independently. UTC "today" is marked in the day slot
    because Reka's own marker follows the browser's local date.
    -->
    <UCalendar
      v-model="calendarRange"
      v-model:placeholder="placeholder"
      range
      :number-of-months="monthCount"
      :max-value="todayDate"
      :is-date-disabled="isDateDisabled"
      :ui="{ cellTrigger: 'data-today:font-normal' }"
      class="mx-auto"
    >
      <template #day="{ day }">
        <span
          :class="
            isUtcToday(day) ? 'underline decoration-2 underline-offset-4' : ''
          "
        >
          {{ day.day }}
          <span v-if="isUtcToday(day)" class="sr-only"> (today, UTC)</span>
        </span>
      </template>
    </UCalendar>

    <!-- Guidance, live selection summary and validation feedback. -->
    <div class="flex flex-col gap-1 text-sm">
      <p class="text-muted">UTC · Up to 30 days, including both dates</p>
      <p class="font-medium" aria-live="polite">{{ summary }}</p>
      <p v-if="feedback" :class="feedbackClass" role="alert">
        {{ feedback.text }}
      </p>
    </div>

    <div class="flex flex-wrap items-center justify-between gap-2">
      <UButton
        label="Clear dates"
        color="neutral"
        variant="ghost"
        :disabled="!startIso && !endIso"
        @click="clearDates"
      />
      <div class="flex items-center gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="subtle"
          @click="emits('cancel')"
        />
        <UButton label="Apply" :disabled="!validation.valid" @click="apply" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import {
  CalendarDate,
  parseDate,
  type DateValue,
} from "@internationalized/date";
import {
  inclusiveDayCount,
  isCalendarDateDisabled,
  validateMetricDateRange,
  type MetricDateRange,
  type MetricPeriod,
} from "@/stores/metric-period";

const props = defineProps<{
  /** Applied period; a custom range seeds the draft, a preset starts empty. */
  period: MetricPeriod;
  /** UTC "today" (yyyy-MM-dd): max selectable date and the partial-day marker. */
  todayUtc: string;
}>();

const emits = defineEmits<{
  /** A complete, valid inclusive UTC range was confirmed. */
  apply: [range: MetricDateRange];
  cancel: [];
}>();

/** Draft endpoints as ISO strings (the single source of truth for validation). */
const startIso = ref<string | null>(
  props.period.mode === "absolute" ? props.period.startDate : null,
);
const endIso = ref<string | null>(
  props.period.mode === "absolute" ? props.period.endDate : null,
);

const todayDate = computed(() => parseDate(props.todayUtc));

/** Visible month: the applied start when reopening a custom range, else the current UTC month. */
const placeholder = shallowRef<DateValue>(
  parseDate(
    props.period.mode === "absolute" ? props.period.startDate : props.todayUtc,
  ),
);

const isWide = useMediaQuery("(min-width: 40rem)");
const monthCount = computed(() => (isWide.value ? 2 : 1));

/** Formats the calendar-system-agnostic fields of a DateValue as yyyy-MM-dd. */
function toIso(value: DateValue | null | undefined): string | null {
  if (!value) {
    return null;
  }
  const month = String(value.month).padStart(2, "0");
  const day = String(value.day).padStart(2, "0");
  return `${String(value.year).padStart(4, "0")}-${month}-${day}`;
}

function toDate(iso: string | null): CalendarDate | null {
  return iso ? parseDate(iso) : null;
}

const startField = computed<CalendarDate | null>({
  get: () => toDate(startIso.value),
  set: (value) => {
    startIso.value = toIso(value);
  },
});

const endField = computed<CalendarDate | null>({
  get: () => toDate(endIso.value),
  set: (value) => {
    endIso.value = toIso(value);
  },
});

/**
 * Calendar range model. A completed pick is normalized to start ≤ end (Reka already
 * orders it; reverse selection is allowed). A lone start is a pending anchor.
 */
const calendarRange = computed<{
  start: DateValue | undefined;
  end: DateValue | undefined;
}>({
  get: () => ({
    start: toDate(startIso.value) ?? undefined,
    end: toDate(endIso.value) ?? undefined,
  }),
  set: (range) => {
    const start = toIso(range.start);
    const end = toIso(range.end);
    if (start && end && end < start) {
      startIso.value = end;
      endIso.value = start;
    } else {
      startIso.value = start;
      endIso.value = end;
    }
  },
});

function isDateDisabled(date: DateValue): boolean {
  return isCalendarDateDisabled(
    toIso(date) ?? "",
    props.todayUtc,
    startIso.value && !endIso.value ? startIso.value : null,
  );
}

function isUtcToday(date: DateValue): boolean {
  return toIso(date) === props.todayUtc;
}

const validation = computed(() =>
  validateMetricDateRange(startIso.value, endIso.value, props.todayUtc),
);

/** Selected date count even when the range is invalid (e.g. "31 days selected"). */
const selectedDays = computed(() =>
  startIso.value && endIso.value
    ? inclusiveDayCount(startIso.value, endIso.value)
    : null,
);

const summary = computed(() => {
  if (!startIso.value) {
    return "Select a start and end date.";
  }
  if (!endIso.value) {
    return "Select an end date.";
  }
  const days = selectedDays.value;
  if (days === null || days < 1) {
    return "No valid range selected.";
  }
  const partial = endIso.value === props.todayUtc ? " · Today is partial" : "";
  return `${days} ${days === 1 ? "day" : "days"} selected${partial}`;
});

/** Inline guidance: hard errors are shown as errors; an incomplete pair is only a hint. */
const feedback = computed(() => {
  const result = validation.value;
  if (result.valid) {
    return null;
  }
  if (result.issue === "missing-start") {
    return null;
  }
  return {
    text: result.message,
    tone: result.issue === "missing-end" ? "hint" : "error",
  } as const;
});

const feedbackClass = computed(() =>
  feedback.value?.tone === "error" ? "text-error" : "text-muted",
);

const fieldColor = computed(() =>
  feedback.value?.tone === "error" ? "error" : "primary",
);

/** Clears both endpoints and the pending anchor (the anchor derives from the endpoints). */
function clearDates() {
  startIso.value = null;
  endIso.value = null;
}

function apply() {
  const result = validation.value;
  if (result.valid && startIso.value && endIso.value) {
    emits("apply", { startDate: startIso.value, endDate: endIso.value });
  }
}
</script>
