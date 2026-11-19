import type { ChildDetail, ChildSummary, ProjectionGeneration, RevisionChild } from "@/lib/children";
import { CANONICAL_CAP, CAP, DIGEST, REV } from "@/lib/revisions.fixtures";

export const CHILD = "0193a000-0000-7000-8000-000000000050";
export const GENERATION = "0193a000-0000-7000-8000-000000000060";

/** Words no child view may show (FR-014): children are declared, never approved, trusted, safe or verified. */
export const BADGE_WORDS = ["approved", "trusted", "safe", "verified", "deprecated", "withdrawn", "active", "lifecycle"];

export const childId = (n: number) => `0193a000-0000-7000-8000-0000000005${String(n).padStart(2, "0")}`;

export const childSummary = (n: number, kind: string, nativeName: string, title: string | null = null, overrides: Partial<ChildSummary> = {}): ChildSummary => ({
  id: childId(n),
  canonicalId: `${CANONICAL_CAP}/${kind}/${childId(n)}`,
  kind,
  ordinal: n,
  duplicate: false,
  firstRevisionId: REV,
  latest: { revisionId: REV, sequence: 1, version: "1.0.0", nativeName, title },
  presentIn: 1,
  createdAt: "2026-11-19T10:15:30.123456Z",
  ...overrides,
});

export const revisionChild = (entryIndex: number, kind: string, nativeName: string, title: string | null = null, duplicate = false): RevisionChild => ({
  entryIndex,
  childId: childId(entryIndex + 1),
  canonicalId: `${CANONICAL_CAP}/${kind}/${childId(entryIndex + 1)}`,
  kind,
  nativeName,
  title,
  duplicate,
});

export const childDetail = (overrides: Partial<ChildSummary> = {}): ChildDetail => ({
  child: childSummary(1, "tool", "search", "Search", overrides),
  capability: { id: CAP, canonicalId: CANONICAL_CAP, displayName: "Weather" },
  history: [
    { revisionId: "0193a000-0000-7000-8000-000000000021", sequence: 1, version: "1.0.0", present: true, nativeName: "search", title: "Search" },
    { revisionId: "0193a000-0000-7000-8000-000000000022", sequence: 2, version: "1.1.0", present: false, nativeName: null, title: null },
  ],
  next: null,
  declaredNotVerified: true,
});

export const generation = (n: number, overrides: Partial<ProjectionGeneration> = {}): ProjectionGeneration => ({
  id: n === 1 ? GENERATION : `0193a000-0000-7000-8000-00000000006${n}`,
  generation: n,
  adapter: { id: "mcp", version: "1" },
  projectionVersion: "1",
  digest: DIGEST,
  byteLength: 321,
  attested: n === 1,
  createdAt: "2026-11-19T10:15:30.123456Z",
  ...(n > 1 ? { comparedToPrevious: "identical" as const } : {}),
  ...overrides,
});
