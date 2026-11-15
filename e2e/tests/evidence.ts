import { mkdirSync } from "node:fs";
import { repoRoot } from "./compose";

/** Demo suite → its spec's working directory (registry `docs/specs/NNN-*`). */
const specDirs = { "goz-0000": "000-repository-bootstrap", "goz-0001": "001-local-identity" } as const;
export type Suite = keyof typeof specDirs;

/**
 * Screenshot directory for a suite. The cumulative runner sets `GOZLEME_DEMO_SHOTS_<SUITE>` so that
 * earlier specs' regression screenshots land in the *current* spec's evidence and merged evidence
 * is never overwritten (IF-08).
 */
export function shotsDir(suite: Suite): string {
  const dir = process.env[`GOZLEME_DEMO_SHOTS_${suite.toUpperCase().replace("-", "_")}`] ?? `${repoRoot}docs/specs/${specDirs[suite]}/evidence/screenshots`;
  mkdirSync(dir, { recursive: true });
  return dir;
}

/** Signed-in browser state for the seeded synthetic account (written by `auth.setup.ts`). */
export const AUTH_STATE = `${repoRoot}.demo-tmp/alice-storage-state.json`;

export const DEMO_PASSWORD = process.env.GOZLEME_DEMO_PASSWORD ?? "local-dev-demo-pass";
export const DEMO = {
  alice: "alice@demo.gozleme.test",
  bob: "bob@demo.gozleme.test",
  lockout: "lockout@demo.gozleme.test",
} as const;
