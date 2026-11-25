/** Reveal a deep-linked finding and move keyboard focus to its confirmation action. */
export function focusFinding(root: HTMLElement | null, occurrence: string) {
  const article = Array.from(
    root?.querySelectorAll<HTMLElement>("article[data-occurrence-key]") ?? [],
  ).find((item) => item.dataset.occurrenceKey === occurrence);
  article?.scrollIntoView({ behavior: "smooth", block: "center" });
  article?.classList.add("ring-2", "ring-primary");
  (
    article?.querySelector<HTMLElement>("[data-proposal-confirm]") ?? article
  )?.focus({ preventScroll: true });
}
