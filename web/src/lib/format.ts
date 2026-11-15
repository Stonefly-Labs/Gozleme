/** Deterministic UTC timestamp for organization views, e.g. `2026-11-15 21:04 UTC`. */
export function formatUtc(iso: string): string {
  return `${new Date(iso).toISOString().slice(0, 16).replace("T", " ")} UTC`;
}
