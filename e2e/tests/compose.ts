import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const repoRoot = fileURLToPath(new URL("../../", import.meta.url));

/** Runs `docker compose <args>` against the repository's compose project. */
export function compose(...args: string[]) {
  execFileSync("docker", ["compose", ...args], { cwd: repoRoot, stdio: "pipe" });
}

/** Values that must never appear in UI, API responses or evidence. */
export const forbiddenStrings = [
  process.env.GOZLEME_APP_PASSWORD ?? "local-dev-app",
  process.env.GOZLEME_MIGRATOR_PASSWORD ?? "local-dev-migrator",
  process.env.GOZLEME_DB_SUPERUSER_PASSWORD ?? "local-dev-superuser",
  "Password=",
  "Username=",
  "Host=postgres",
  "Exception",
];
