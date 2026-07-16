/**
 * Computes a stable SHA-256 hex digest of a finding's identifying content so
 * the cart store can detect duplicates and toggle add/remove. The hash covers
 * file, line, and summary — the fields that together make a finding unique
 * within a review (the body can vary between re-reviews).
 */
export async function computeFindingContentHash(finding: {
  file: string;
  line?: number | string | null;
  summary: string;
}): Promise<string> {
  const payload = `${finding.file}:${finding.line ?? "no-line"}:${finding.summary}`;
  const encoded = new TextEncoder().encode(payload);
  const buffer = await crypto.subtle.digest("SHA-256", encoded);

  return Array.from(new Uint8Array(buffer))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
