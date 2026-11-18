import { createHmac } from "node:crypto";
import { execFileSync } from "node:child_process";
import { repoRoot } from "./compose";

const CONTEXT_KEY = process.env.GOZLEME_CONTEXT_KEY ?? "local-dev-context-key-not-a-secret-0001";

/**
 * One psql session inside the Compose `postgres` service as `role` (`gozleme_app`, `gozleme_migrator` or the
 * superuser `postgres`): the last output line, or the SQLSTATE of the error.
 */
export function psql(role: string, sql: string): { out: string; state?: string } {
  try {
    const out = execFileSync("docker", ["compose", "exec", "-T", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-v", "VERBOSITY=verbose", "-U", role, "-d", "gozleme", "-f", "-"], {
      cwd: repoRoot,
      input: sql,
      stdio: "pipe",
      maxBuffer: 64 * 1024 * 1024,
    }).toString();
    return { out: out.trim().split("\n").pop() ?? "" };
  } catch (e) {
    const stderr = String((e as { stderr?: Buffer }).stderr ?? "");
    return { out: "", state: /ERROR:\s+([0-9A-Z]{5}):/.exec(stderr)?.[1] ?? "unknown" };
  }
}

/** The API's tenant-context token (GOZ-0003 contracts/tenant-context.md §1), for database-level evidence only. */
export function sign(accountId: string, expires = Math.floor(Date.now() / 1000) + 300) {
  const payload = `v1.${accountId}.${expires}`;
  return `${payload}.${createHmac("sha256", CONTEXT_KEY).update(payload, "utf8").digest("hex")}`;
}

const context = (account: string | null) => (account ? `DO $$BEGIN PERFORM set_config('gozleme.account_context', '${sign(account)}', true); END$$;\n` : "");

/** A DML `statement` as `role` in `account`'s signed context (or none), rolled back: "<n> row(s)" or the SQLSTATE. */
export function attempt(role: string, account: string | null, statement: string): string {
  const { out, state } = psql(role, `BEGIN;\n${context(account)}WITH c AS (${statement} RETURNING 1) SELECT count(*) FROM c;\nROLLBACK;\n`);
  return state ?? `${out} row(s)`;
}

/** Any `statement` (e.g. TRUNCATE) as `role` in `account`'s context, rolled back: "ok" or the SQLSTATE. */
export function execute(role: string, account: string | null, statement: string): string {
  const { state } = psql(role, `BEGIN;\n${context(account)}${statement};\nROLLBACK;\n`);
  return state ?? "ok";
}

/** As the superuser with RLS bypassed and triggers disabled (`session_replication_role = replica`): tamper only. */
export function tamper(statement: string): string {
  const { out, state } = psql("postgres", `BEGIN;\nSET LOCAL session_replication_role = replica;\nWITH c AS (${statement} RETURNING 1) SELECT count(*) FROM c;\nCOMMIT;\n`);
  return state ?? out;
}
