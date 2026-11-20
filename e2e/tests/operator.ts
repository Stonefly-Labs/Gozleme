import { spawnSync } from "node:child_process";
import { repoRoot } from "./compose";

/**
 * `docker compose run --rm --no-deps migrate <args>`: an operator one-shot command (GOZ-0005 `handle-*`, GOZ-0007
 * `signing-key-succession`, GOZ-0008 `projection-regenerate`), its exit code and output. Never reachable over HTTP.
 */
export function operator(args: string[], env: Record<string, string> = {}) {
  const flags = Object.entries(env).flatMap(([k, v]) => ["-e", `${k}=${v}`]);
  const r = spawnSync("docker", ["compose", "run", "--rm", "--no-deps", ...flags, "migrate", ...args], { cwd: repoRoot, encoding: "utf8" });
  return { exit: r.status ?? -1, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
}

export const reclaim = (handle: string) => operator(["handle-reclaim", "--handle", handle]);

export const succession = (publisherId: string, reason: "lost" | "compromised") => operator(["signing-key-succession", "--publisher", publisherId, "--reason", reason]);

/** GOZ-0008: re-projects a stored revision, adding its unattested generation N + 1. */
export const projectionRegenerate = (revisionId: string) => operator(["projection-regenerate", "--revision", revisionId]);
