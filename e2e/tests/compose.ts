import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const repoRoot = fileURLToPath(new URL("../../", import.meta.url));

/** Runs `docker compose <args>` against the repository's compose project. */
export function compose(...args: string[]) {
  execFileSync("docker", ["compose", ...args], { cwd: repoRoot, stdio: "pipe" });
}

/** `docker compose <args>` with extra environment (e.g. recreating the API with registration closed). */
export function composeWithEnv(env: Record<string, string>, ...args: string[]) {
  execFileSync("docker", ["compose", ...args], { cwd: repoRoot, stdio: "pipe", env: { ...process.env, ...env } });
}

/** Values that must never appear in UI, API responses or evidence. */
export const forbiddenStrings = [
  process.env.GOZLEME_APP_PASSWORD ?? "local-dev-app",
  process.env.GOZLEME_MIGRATOR_PASSWORD ?? "local-dev-migrator",
  process.env.GOZLEME_DB_SUPERUSER_PASSWORD ?? "local-dev-superuser",
  process.env.GOZLEME_GRAFANA_ADMIN_PASSWORD ?? "local-dev-grafana",
  "Password=",
  "Username=",
  "Host=postgres",
  process.env.GOZLEME_DEMO_PASSWORD ?? "local-dev-demo-pass",
  "Exception",
];

/** Credential material only; observability tools legitimately show exception type names. */
export const secretStrings = forbiddenStrings.filter((s) => s !== "Exception");
