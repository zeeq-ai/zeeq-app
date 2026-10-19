<template>
  <!--
  Reporting-period summary shown for custom UTC ranges (rolling presets need none): the
  full dates with year and UTC, whether today's partial data or a completed historical
  period is displayed, and a generic coverage note. The note is deliberately
  policy-independent — it never asserts a retention window, because older telemetry may
  be missing while review history can still exist.
  -->
  <div
    v-if="period.mode === 'absolute'"
    class="flex flex-col gap-0.5 text-xs text-muted"
  >
    <p>
      <span class="font-medium text-default">Reporting period:</span>
      {{ label }}
      <template v-if="includesToday"> · Today is partial</template>
      <template v-if="historical">
        · Historical period · manual refresh</template
      >
    </p>
    <p>
      Older telemetry may be unavailable. Review history may still be available.
    </p>
  </div>
</template>

<script setup lang="ts">
import {
  isHistoricalPeriod,
  metricPeriodLabel,
  periodIncludesToday,
  type MetricPeriod,
} from "@/stores/metric-period";

const props = defineProps<{
  period: MetricPeriod;
  /** UTC "today" (yyyy-MM-dd) used to classify the range as live or historical. */
  todayUtc: string;
}>();

const label = computed(() => metricPeriodLabel(props.period));
const includesToday = computed(() =>
  periodIncludesToday(props.period, props.todayUtc),
);
const historical = computed(() =>
  isHistoricalPeriod(props.period, props.todayUtc),
);
</script>
