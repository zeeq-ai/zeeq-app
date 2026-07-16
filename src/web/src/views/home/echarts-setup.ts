/**
 * One-time, tree-shaken ECharts registration for the home dashboard.
 *
 * vue-echarts renders whatever is registered here; importing this module for its
 * side effect (from `MetricChart.vue` only) keeps the bundle to the renderer,
 * chart types, and components the metrics panels actually use — line/bar for the
 * time series (UI-1..UI-6), scatter for duration-vs-tokens (UI-8/UI-9), pie for
 * the review-origin donut (UI-5), plus the grid/tooltip/legend/dataZoom/title
 * components shared across every panel.
 *
 * `use()` is idempotent, so repeated imports register each component once.
 */
import { registerTheme, use } from "echarts/core";
import { CanvasRenderer } from "echarts/renderers";
import { BarChart, LineChart, PieChart, ScatterChart } from "echarts/charts";
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  TitleComponent,
  TooltipComponent,
} from "echarts/components";
import waldenTheme from "./echarts-theme-walden.json";
import waldenDarkTheme from "./echarts-theme-walden-dark.json";
import chalkTheme from "./echarts-theme-chalk.json";

use([
  CanvasRenderer,
  LineChart,
  BarChart,
  ScatterChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DataZoomComponent,
  TitleComponent,
]);

/** Light-mode chart theme (blue/teal/lavender palette). */
registerTheme("walden", waldenTheme);
/**
 * Dark-mode variant of the same walden palette — identical except for a
 * subtle split-line color, since walden's default (`#eeeeee`) reads as a
 * near-white line on a dark background instead of a faint grid.
 */
registerTheme("walden-dark", waldenDarkTheme);
/** Unused now that walden covers both modes; kept registered in case a panel opts back in. */
registerTheme("chalk", chalkTheme);
