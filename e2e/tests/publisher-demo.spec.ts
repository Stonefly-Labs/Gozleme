import AxeBuilder from "@axe-core/playwright";
import { createHmac } from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { composeWithEnv, repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, PARTNER_ORG, evidenceDir, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0005 autonomous demo (quickstart steps 1–11): in Seeded Demo Org alice (admin) registers a publisher
 * handle and carol (publisher) creates a capability whose canonical ID and alias survive two renames; dave
 * cannot take the handle and gets the same answer as alice's own retry; a duplicate name conflicts. The API
 * role × action matrix, the database's refusals for both logins and the origin mismatch refusal are recorded
 * as evidence. An operator earmarks and blocks handles and reclaims a squatted one: the squatter's publisher
 * is suspended with unchanged IDs and its aliases stay retired. Every write is traced, counted and audited with
 * opaque IDs only.
 */
const shots = shotsDir("goz-0005");
const evidence = evidenceDir("goz-0005");
const suiteStarted = Date.now();
const CONTEXT_KEY = process.env.GOZLEME_CONTEXT_KEY ?? "local-dev-context-key-not-a-secret-0001";
const ORIGIN = process.env.GOZLEME_REGISTRY_ORIGIN ?? "registry.demo.gozleme.test";
const OTHER_ORIGIN = "other.demo.gozleme.test";
const TOKEN = /v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.\d+\.[0-9a-f]{64}/;
const UUID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const HANDLE_UNAVAILABLE = "That handle isn't available.";
const NAME_CONFLICT = "That name is already used under this publisher.";
const MISMATCH = "configured origin differs from the recorded origin";

// Handles are permanent and instance-wide, so every run uses fresh synthetic ones.
const run = String(Math.floor(100000 + Math.random() * 900000));
const HANDLE = `demo-${run}`;
const EARMARKED = `acme-${run}`;
const BLOCKED = `blocked-${run}`;
const SQUATTED = `squat-${run}`;
const DISPLAY = ["Demo Tools", "Weather Lookup", "Weather Lookup v2", "Forecasts", "Squatter Tools", "Squatted Forecast"];

const orgApi = (org: string) => `${API_URL}/api/v1/organizations/${org}`;
const ORG_API = orgApi(DEMO_ORG.id);
const PARTNER_API = orgApi(PARTNER_ORG.id);

let alice: Page;
let carol: Page;
let dave: Page;
let aliceId: string;
let carolId: string;
let daveId: string;
let publisherId: string;
let capability: { id: string; canonicalId: string; alias: string };
let us1ElapsedSeconds = 0;
const operator: Record<string, { exit: number; stdout: string }> = {};

test.describe.configure({ mode: "serial" });

async function expectAccessible(page: Page) {
  await expect(page).toHaveTitle(/ — Gözleme$|^Gözleme — /);
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
}

async function signedInPage(browser: Browser, email: string) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, colorScheme: "light" });
  const page = await context.newPage();
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(DEMO_PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("account-email")).toHaveText(email);
  return page;
}

/** A direct API call with the web-client header (CSRF layer 2), as a scripted client would send it. */
async function api(page: Page, method: string, url: string, data?: unknown) {
  const res = await page.request.fetch(url, {
    method,
    headers: { "x-gozleme-client": "web", "content-type": "application/json" },
    data: data === undefined ? undefined : JSON.stringify(data),
  });
  const text = await res.text();
  return { status: res.status(), text, json: text ? (JSON.parse(text) as Record<string, unknown>) : null, cache: res.headers()["cache-control"] };
}

const setRoles = (page: Page, org: string, userId: string, roles: string[]) => api(page, "PUT", `${orgApi(org)}/members/${userId}/roles`, { roles });
const register = (page: Page, org: string, handle: string, displayName = DISPLAY[0]) => api(page, "POST", `${orgApi(org)}/publishers`, { handle, displayName });
const userId = async (page: Page) => String((await (await page.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId);

/** One psql session inside the postgres container as `role`; SQLSTATE in errors. */
function psql(role: string, sql: string): { out: string; state?: string } {
  try {
    const out = execFileSync("docker", ["compose", "exec", "-T", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-v", "VERBOSITY=verbose", "-U", role, "-d", "gozleme", "-f", "-"], {
      cwd: repoRoot,
      input: sql,
      stdio: "pipe",
    }).toString();
    return { out: out.trim().split("\n").pop() ?? "" };
  } catch (e) {
    const stderr = String((e as { stderr?: Buffer }).stderr ?? "");
    return { out: "", state: /ERROR:\s+([0-9A-Z]{5}):/.exec(stderr)?.[1] ?? "unknown" };
  }
}

/** The API's token construction (GOZ-0003 contracts/tenant-context.md §1), for database-level evidence only. */
function sign(accountId: string, expires = Math.floor(Date.now() / 1000) + 300) {
  const payload = `v1.${accountId}.${expires}`;
  return `${payload}.${createHmac("sha256", CONTEXT_KEY).update(payload, "utf8").digest("hex")}`;
}

/** `statement` as `role` in `account`'s signed context (or none), always rolled back; rows affected or the SQLSTATE. */
function attempt(role: string, account: string | null, statement: string): string {
  const context = account ? `DO $$BEGIN PERFORM set_config('gozleme.account_context', '${sign(account)}', true); END$$;\n` : "";
  const { out, state } = psql(role, `BEGIN;\n${context}WITH c AS (${statement} RETURNING 1) SELECT count(*) FROM c;\nROLLBACK;\n`);
  return state ?? `${out} row(s)`;
}

/** `docker compose run --rm migrate <args>`: the operator's one-shot command, its exit code and its output. */
function migrate(args: string[], env: Record<string, string> = {}) {
  const flags = Object.entries(env).flatMap(([k, v]) => ["-e", `${k}=${v}`]);
  const r = spawnSync("docker", ["compose", "run", "--rm", "--no-deps", ...flags, "migrate", ...args], { cwd: repoRoot, encoding: "utf8" });
  return { exit: r.status ?? -1, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
}

function snapshot() {
  return psql(
    "postgres",
    `SELECT concat_ws('|',
       (SELECT string_agg(concat_ws(',', id, organization_id, handle, created_at), ';' ORDER BY id) FROM gozleme.publishers WHERE id = '${publisherId}'),
       (SELECT string_agg(concat_ws(',', id, publisher_id, name, created_at), ';' ORDER BY id) FROM gozleme.capabilities WHERE publisher_id = '${publisherId}'),
       (SELECT string_agg(concat_ws(',', id, handle, status), ';' ORDER BY id) FROM gozleme.handle_claims WHERE publisher_id = '${publisherId}'),
       (SELECT string_agg(concat_ws(',', alias, capability_id), ';' ORDER BY alias) FROM gozleme.alias_reservations WHERE capability_id = '${capability.id}'))`,
  ).out;
}

test.afterAll(async () => {
  // Restore the seeded roles even if a step failed midway: carol {member, publisher}, dave admin of the partner org.
  if (alice && carolId) await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"]);
  if (dave && daveId) await setRoles(dave, PARTNER_ORG.id, daveId, ["admin"]);
  composeWithEnv({ GOZLEME_REGISTRY_ORIGIN: ORIGIN }, "up", "-d", "--wait", "--no-deps", "api");
  for (const page of [alice, carol, dave]) await page?.context().close();
});

test("an admin registers a publisher and a publisher member creates and renames a capability with permanent IDs", async ({ browser }) => {
  alice = await signedInPage(browser, DEMO.alice);
  carol = await signedInPage(browser, DEMO.carol);
  dave = await signedInPage(browser, DEMO.dave);
  [aliceId, carolId, daveId] = [await userId(alice), await userId(carol), await userId(dave)];
  // The seed grants carol publisher; the GOZ-0004 demo before this one leaves her a plain member.
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);
  // Registration budgets are shared and persistent (research R8); a rerun within the hour starts from zero.
  psql("postgres", `DELETE FROM gozleme.publisher_registration_attempts WHERE account_id IN ('${aliceId}', '${carolId}', '${daveId}')`);

  const started = Date.now();
  await alice.goto(`/organizations/${DEMO_ORG.id}`);
  const form = alice.getByRole("form", { name: "Register a publisher" });
  await expect(form.getByLabel("Handle")).toHaveValue("");
  await form.getByLabel("Handle").fill(HANDLE);
  await form.getByLabel("Display name").fill(DISPLAY[0]);
  await form.getByRole("button", { name: "Register publisher" }).click();
  const confirmation = alice.getByTestId("register-confirmation");
  await expect(confirmation.getByTestId("confirmed-handle")).toHaveText(HANDLE);
  await expect(confirmation.getByTestId("confirmed-organization")).toHaveText(DEMO_ORG.id);
  await expect(confirmation.getByTestId("confirmed-publisher-id")).toHaveText(new RegExp(`^gz://${ORIGIN.replaceAll(".", "\\.")}/pub/${UUID}$`));
  await expect(alice.getByTestId("publisher-row").filter({ hasText: HANDLE })).toBeVisible();
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/01-admin-registers-publisher.png`, fullPage: true });

  const listed = (await api(alice, "GET", `${ORG_API}/publishers`)).json!.publishers as { id: string; handle: string; publisherId: string }[];
  const publisher = listed.find((p) => p.handle === HANDLE)!;
  publisherId = publisher.id;
  expect(publisher.publisherId).toBe(`gz://${ORIGIN}/pub/${publisherId}`);
  expect(publisher.publisherId).not.toContain(DEMO_ORG.id);

  // alice is an admin but not a publisher: no capability form.
  await alice.goto(`/organizations/${DEMO_ORG.id}/publishers/${publisherId}`);
  await expect(alice.getByTestId("publisher-handle")).toHaveText(HANDLE);
  await expect(alice.getByRole("form", { name: "Create a capability" })).toHaveCount(0);

  await carol.goto(`/organizations/${DEMO_ORG.id}/publishers/${publisherId}`);
  const create = carol.getByRole("form", { name: "Create a capability" });
  await create.getByLabel("Name", { exact: true }).fill("weather-lookup");
  await create.getByLabel("Display name").fill(DISPLAY[1]);
  await create.getByRole("button", { name: "Create capability" }).click();
  const row = carol.getByTestId("capability-row").filter({ hasText: `${HANDLE}/weather-lookup` });
  await expect(row.getByTestId("capability-display-name")).toHaveText(DISPLAY[1]);
  const canonicalId = (await row.getByTestId("capability-canonical-id").textContent())!;
  expect(canonicalId).toMatch(new RegExp(`^gz://${ORIGIN.replaceAll(".", "\\.")}/cap/${UUID}$`));
  await expect(row.getByTestId("capability-alias")).toHaveText(`${HANDLE}/weather-lookup`);
  capability = { id: canonicalId.split("/").pop()!, canonicalId, alias: `${HANDLE}/weather-lookup` };
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/02-publisher-creates-capability.png`, fullPage: true });

  for (const name of [DISPLAY[2], DISPLAY[3]]) {
    const rename = row.getByRole("form", { name: "Rename Display name of weather-lookup" });
    await rename.getByLabel("Display name of weather-lookup").fill(name);
    await rename.getByRole("button", { name: "Rename" }).click();
    await expect(row.getByTestId("capability-display-name")).toHaveText(name);
    await expect(row.getByTestId("capability-canonical-id")).toHaveText(capability.canonicalId);
    await expect(row.getByTestId("capability-alias")).toHaveText(capability.alias);
  }
  us1ElapsedSeconds = (Date.now() - started) / 1000;
  expect(us1ElapsedSeconds).toBeLessThan(120); // SC-001
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/03-renamed-twice-ids-unchanged.png`, fullPage: true });

  const read = (await api(carol, "GET", `${ORG_API}/publishers/${publisherId}/capabilities/${capability.id}`)).json!;
  expect({ canonicalId: read.canonicalId, alias: read.alias, displayName: read.displayName }).toEqual({ canonicalId: capability.canonicalId, alias: capability.alias, displayName: DISPLAY[3] });
});

test("a taken handle gets one identical answer and a duplicate capability name conflicts", async () => {
  const foreign = await register(dave, PARTNER_ORG.id, HANDLE, DISPLAY[4]);
  const own = await register(alice, DEMO_ORG.id, HANDLE);
  expect(foreign.json).toEqual({ error: "handle_unavailable" });
  expect(`${foreign.status}|${foreign.text}|${foreign.cache}`).toBe(`${own.status}|${own.text}|${own.cache}`);
  expect(foreign.status).toBe(409);

  await dave.goto(`/organizations/${PARTNER_ORG.id}`);
  const form = dave.getByRole("form", { name: "Register a publisher" });
  await form.getByLabel("Handle").fill(HANDLE);
  await form.getByLabel("Display name").fill(DISPLAY[4]);
  await form.getByRole("button", { name: "Register publisher" }).click();
  await expect(dave.getByTestId("register-error")).toHaveText(HANDLE_UNAVAILABLE);
  await expect(form.getByLabel("Handle")).toHaveValue(HANDLE);
  await expectAccessible(dave);
  await dave.screenshot({ path: `${shots}/04-taken-handle-unavailable.png`, fullPage: true });

  await carol.goto(`/organizations/${DEMO_ORG.id}/publishers/${publisherId}`);
  const create = carol.getByRole("form", { name: "Create a capability" });
  await create.getByLabel("Name", { exact: true }).fill("weather-lookup");
  await create.getByLabel("Display name").fill("Duplicate");
  await create.getByRole("button", { name: "Create capability" }).click();
  await expect(carol.getByTestId("capability-error")).toHaveText(NAME_CONFLICT);
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/05-duplicate-name-conflicts.png`, fullPage: true });
  const duplicate = await api(carol, "POST", `${ORG_API}/publishers/${publisherId}/capabilities`, { name: "weather-lookup", displayName: "Duplicate" });
  expect(`${duplicate.status} ${duplicate.text}`).toBe('409 {"error":"conflict"}');
});

test("evidence: API role × action matrix, database refusals for both logins and the origin mismatch", async () => {
  test.setTimeout(240_000);
  const capabilityApi = `${ORG_API}/publishers/${publisherId}/capabilities`;
  const matrix: Record<string, Record<string, string>> = {};
  for (const role of ["", "publisher", "reviewer", "approver", "admin"]) {
    expect((await setRoles(alice, DEMO_ORG.id, carolId, role ? [role] : [])).status).toBe(200);
    const permitted = ((await api(carol, "GET", ORG_API)).json!.permissions as string[]) ?? [];
    // Writes that would be allowed are sent invalid or unchanged, so the matrix creates nothing permanent.
    const calls: [string, () => ReturnType<typeof api>][] = [
      ["publisher.list", () => api(carol, "GET", `${ORG_API}/publishers`)],
      ["publisher.read", () => api(carol, "GET", `${ORG_API}/publishers/${publisherId}`)],
      ["publisher.register", () => register(carol, DEMO_ORG.id, "X")],
      ["publisher.rename", () => api(carol, "PATCH", `${ORG_API}/publishers/${publisherId}`, { displayName: DISPLAY[0] })],
      ["capability.list", () => api(carol, "GET", capabilityApi)],
      ["capability.read", () => api(carol, "GET", `${capabilityApi}/${capability.id}`)],
      ["capability.create", () => api(carol, "POST", capabilityApi, { name: "X", displayName: "X" })],
      ["capability.rename", () => api(carol, "PATCH", `${capabilityApi}/${capability.id}`, { displayName: DISPLAY[3] })],
    ];
    const row: Record<string, string> = {};
    for (const [action, call] of calls) {
      const { status } = await call();
      const outcome = status === 403 ? "deny" : "allow";
      expect(outcome, `${role || "member"} ${action} (${status})`).toBe(permitted.includes(action) ? "allow" : "deny");
      row[action] = `${outcome} (${status})`;
    }
    matrix[role ? `member+${role}` : "member"] = row;
  }
  expect((await setRoles(alice, DEMO_ORG.id, carolId, ["publisher"])).status).toBe(200);
  expect(matrix.member["publisher.read"]).toBe("allow (200)");
  expect(matrix["member+admin"]["capability.create"]).toBe("deny (403)");
  expect(matrix["member+publisher"]["capability.create"]).toBe("allow (400)");

  const nonMember = await api(dave, "GET", `${ORG_API}/publishers/${publisherId}`);
  const unknown = await api(dave, "GET", `${orgApi(crypto.randomUUID())}/publishers/${publisherId}`);
  expect(`${nonMember.status}|${nonMember.text}|${nonMember.cache}`).toBe("404||no-store");
  expect(`${unknown.status}|${unknown.text}|${unknown.cache}`).toBe(`${nonMember.status}|${nonMember.text}|${nonMember.cache}`);

  const before = snapshot();
  const statements: Record<string, string> = {
    "change a handle": `UPDATE gozleme.publishers SET handle = 'changed-${run}' WHERE id = '${publisherId}'`,
    "move a publisher to another organization": `UPDATE gozleme.publishers SET organization_id = '${PARTNER_ORG.id}' WHERE id = '${publisherId}'`,
    "change a publisher id": `UPDATE gozleme.publishers SET id = gen_random_uuid() WHERE id = '${publisherId}'`,
    "change a capability name": `UPDATE gozleme.capabilities SET name = 'changed' WHERE id = '${capability.id}'`,
    "change a capability id": `UPDATE gozleme.capabilities SET id = gen_random_uuid() WHERE id = '${capability.id}'`,
    "delete a publisher": `DELETE FROM gozleme.publishers WHERE id = '${publisherId}'`,
    "delete a capability": `DELETE FROM gozleme.capabilities WHERE id = '${capability.id}'`,
    "delete a handle claim": `DELETE FROM gozleme.handle_claims WHERE publisher_id = '${publisherId}'`,
    "change an alias": `UPDATE gozleme.alias_reservations SET alias = 'x/y' WHERE capability_id = '${capability.id}'`,
  };
  const database: Record<string, Record<string, string>> = {};
  for (const [label, sql] of Object.entries(statements)) {
    database[label] = {
      "gozleme_app as admin": attempt("gozleme_app", aliceId, sql),
      "gozleme_app as publisher": attempt("gozleme_app", carolId, sql),
      "owner as admin": attempt("gozleme_migrator", aliceId, sql),
      "owner without context": attempt("gozleme_migrator", null, sql),
    };
    for (const [actor, outcome] of Object.entries(database[label])) expect(outcome, `${actor}: ${label}`).toMatch(/^([0-9A-Z]{5}|0 row\(s\))$/);
  }
  expect(snapshot()).toBe(before);

  // Origin mismatch (SC-003): the migrator refuses before migrating and the API refuses to start.
  const migrator = migrate([], { Gozleme__Registry__Origin: OTHER_ORIGIN });
  expect(migrator.exit).not.toBe(0);
  expect(`${migrator.stdout}\n${migrator.stderr}`).toContain(MISMATCH);
  const recreated = Date.now();
  let apiOutcome = "";
  try {
    composeWithEnv({ GOZLEME_REGISTRY_ORIGIN: OTHER_ORIGIN }, "up", "-d", "--no-deps", "--force-recreate", "api");
    await expect
      .poll(() => execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(recreated).toISOString(), "api"], { cwd: repoRoot }).toString().includes(MISMATCH), {
        timeout: 60_000,
        intervals: [2000],
      })
      .toBe(true);
    // The host throws on the mismatch and exits; the restart policy then restarts it, and it refuses again.
    const container = execFileSync("docker", ["compose", "ps", "-aq", "api"], { cwd: repoRoot }).toString().trim();
    const restarts = () => Number(execFileSync("docker", ["inspect", "--format", "{{.RestartCount}}", container]).toString().trim());
    await expect.poll(restarts, { timeout: 60_000, intervals: [2000] }).toBeGreaterThan(0);
    apiOutcome = `refused to start (${MISMATCH}); restarted ${restarts()} time(s) by its restart policy, never served`;
    expect((await api(alice, "GET", `${ORG_API}/publishers`).catch(() => ({ status: 0 }))).status).not.toBe(200);
  } finally {
    composeWithEnv({ GOZLEME_REGISTRY_ORIGIN: ORIGIN }, "up", "-d", "--wait", "--no-deps", "--force-recreate", "api");
  }
  const restored = (await api(carol, "GET", `${ORG_API}/publishers/${publisherId}/capabilities/${capability.id}`)).json!;
  expect({ canonicalId: restored.canonicalId, alias: restored.alias }).toEqual({ canonicalId: capability.canonicalId, alias: capability.alias });

  const record = {
    spec: "GOZ-0005",
    capturedAt: new Date().toISOString(),
    origin: ORIGIN,
    organization: DEMO_ORG.id,
    publisher: { id: publisherId, handle: HANDLE },
    capability,
    us1ElapsedSeconds,
    handleUnavailableIdenticalForForeignAndOwner: true,
    apiMatrixForCarol: matrix,
    nonMemberAndUnknownOrganization: `${nonMember.status} (identical, empty, no-store)`,
    databaseRefusals: database,
    rowsUnchanged: true,
    originMismatch: { migrator: `exit ${migrator.exit}: ${MISMATCH}`, api: apiOutcome, restoredIdsUnchanged: true },
  };
  writeEvidence(record);
});

test("an operator earmarks, blocks and reclaims handles; the reclaimed publisher is suspended with unchanged IDs", async () => {
  test.setTimeout(240_000);
  for (const [key, args] of [
    ["reserve earmarked", ["handle-reserve", "--handle", EARMARKED, "--organization", DEMO_ORG.id]],
    ["reserve blocked", ["handle-reserve", "--handle", BLOCKED]],
  ] as const) {
    const r = migrate([...args]);
    expect(r.exit, r.stderr).toBe(0);
    expect(r.stdout).toMatch(new RegExp(`^Reserved handle claim ${UUID} \\((earmarked for organization ${DEMO_ORG.id}|blocked)\\)$`, "m"));
    operator[key] = { exit: r.exit, stdout: r.stdout.split("\n").find((l) => l.startsWith("Reserved"))! };
  }

  const taken = await register(alice, DEMO_ORG.id, HANDLE);
  for (const handle of [EARMARKED, BLOCKED]) {
    const r = await register(dave, PARTNER_ORG.id, handle, DISPLAY[4]);
    expect(`${r.status}|${r.text}|${r.cache}`).toBe(`${taken.status}|${taken.text}|${taken.cache}`);
  }
  expect((await register(alice, DEMO_ORG.id, EARMARKED)).status).toBe(201);

  // dave squats a handle and publishes under it.
  expect((await setRoles(dave, PARTNER_ORG.id, daveId, ["admin", "publisher"])).status).toBe(200);
  const squat = await register(dave, PARTNER_ORG.id, SQUATTED, DISPLAY[4]);
  expect(squat.status).toBe(201);
  const squatId = String(squat.json!.id);
  const forecast = await api(dave, "POST", `${PARTNER_API}/publishers/${squatId}/capabilities`, { name: "forecast", displayName: DISPLAY[5] });
  expect(forecast.status).toBe(201);

  const reclaim = migrate(["handle-reclaim", "--handle", SQUATTED, "--organization", DEMO_ORG.id]);
  expect(reclaim.exit, reclaim.stderr).toBe(0);
  expect(reclaim.stdout).toContain(`publisher ${squatId} of organization ${PARTNER_ORG.id} suspended`);
  operator.reclaim = { exit: reclaim.exit, stdout: reclaim.stdout.split("\n").filter((l) => l.startsWith("Reclaimed") || l.startsWith("Reserved")).join(" / ") };
  // The full output, JSON logs included, never repeats the operator's handle.
  for (const h of [EARMARKED, BLOCKED, SQUATTED]) expect(reclaim.stdout + reclaim.stderr).not.toContain(h);

  await dave.goto(`/organizations/${PARTNER_ORG.id}/publishers/${squatId}`);
  await expect(dave.getByTestId("suspended-notice")).toBeVisible();
  await expect(dave.getByTestId("suspended-badge")).toHaveCount(2); // the publisher and its capability
  await expect(dave.getByTestId("publisher-id")).toHaveText(String(squat.json!.publisherId));
  await expect(dave.getByTestId("capability-canonical-id")).toHaveText(String(forecast.json!.canonicalId));
  await expect(dave.getByTestId("capability-alias")).toHaveText(`${SQUATTED}/forecast`);
  await expect(dave.getByRole("form")).toHaveCount(0);
  await expectAccessible(dave);
  await dave.screenshot({ path: `${shots}/06-reclaimed-publisher-suspended.png`, fullPage: true });
  const frozen = await api(dave, "POST", `${PARTNER_API}/publishers/${squatId}/capabilities`, { name: "radar", displayName: "Radar" });
  expect(`${frozen.status} ${frozen.text}`).toBe('409 {"error":"suspended"}');

  const reRegistered = await register(alice, DEMO_ORG.id, SQUATTED);
  expect(reRegistered.status).toBe(201);
  const heldId = String(reRegistered.json!.id);
  const retired = await api(carol, "POST", `${ORG_API}/publishers/${heldId}/capabilities`, { name: "forecast", displayName: "Forecast" });
  expect(`${retired.status} ${retired.text}`).toBe('409 {"error":"conflict"}');
  expect((await api(carol, "POST", `${ORG_API}/publishers/${heldId}/capabilities`, { name: "radar", displayName: "Radar" })).status).toBe(201);
  expect((await setRoles(dave, PARTNER_ORG.id, daveId, ["admin"])).status).toBe(200);

  // Boundary (FR-025, FR-032): no catalog, publish or operator route over HTTP.
  const boundary: Record<string, number> = {};
  for (const [method, url] of [
    ["GET", `${API_URL}/api/v1/capabilities`],
    ["POST", `${API_URL}/api/v1/publish`],
    ["GET", `${API_URL}/api/v1/handles`],
    ["POST", `${API_URL}/api/v1/handles/reclaim`],
    ["POST", `${ORG_API}/publishers/${squatId}/reclaim`],
    ["GET", `${API_URL}/api/v1/reservations`],
    ["POST", `${PARTNER_API}/publishers/${squatId}/unsuspend`],
    ["POST", `${ORG_API}/publishers/${heldId}/transfer`],
  ] as const) {
    const { status } = await api(alice, method, url, method === "GET" ? undefined : {});
    expect([404, 405], `${method} ${url}`).toContain(status);
    boundary[`${method} ${url.replace(API_URL, "").replaceAll(squatId, "{publisherId}").replaceAll(heldId, "{publisherId}")}`] = status;
  }

  const record = JSON.parse(readEvidence());
  writeEvidence({
    ...record,
    reclaim: {
      handles: { earmarked: EARMARKED, blocked: BLOCKED, squatted: SQUATTED },
      operator,
      squatterPublisher: { id: squatId, state: "suspended", publisherId: squat.json!.publisherId, capability: forecast.json!.canonicalId },
      reRegisteredBy: DEMO_ORG.id,
      retiredAliasRefused: `${retired.status} conflict`,
    },
    boundary,
  });
});

test("observability: publisher writes and operator commands are traced, counted and audited with opaque IDs only", async ({ request }) => {
  test.setTimeout(240_000);
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = [...(await recentSpans("gozleme-api", 15, "gozleme.publisher.register")), ...(await recentSpans("gozleme-api", 15, "gozleme.capability.create"))];
        return ["gozleme.publisher.register", "gozleme.capability.create"].every((n) => spans.some((s) => s.name === n && s.attributes["gozleme.publisher.outcome"] === "success"));
      },
      { timeout: 60_000, intervals: [2000] },
    )
    .toBe(true);
  const registered = spans.find((s) => s.name === "gozleme.publisher.register" && s.attributes["gozleme.publisher.id"] === publisherId)!;
  expect(registered.attributes["gozleme.organization.id"]).toBe(DEMO_ORG.id);

  let metrics = "";
  await expect
    .poll(
      async () => {
        metrics = await (await request.get(METRICS_URL)).text();
        const lines = metrics.split("\n").filter((l) => l.startsWith("gozleme_publisher_operations_total{"));
        return ["handle_unavailable", "conflict", "suspended", "success"].every((o) => lines.some((l) => l.includes(`gozleme_publisher_outcome="${o}"`)));
      },
      { timeout: 90_000, intervals: [5000] },
    )
    .toBe(true);
  for (const line of metrics.split("\n").filter((l) => l.startsWith("gozleme_publisher_operations_total{"))) {
    const labels = [...line.matchAll(/(\w+)="/g)].map((m) => m[1]).filter((k) => k.startsWith("gozleme_"));
    expect(labels.sort()).toEqual(["gozleme_publisher_operation", "gozleme_publisher_outcome"]);
  }

  const auditQuery = '{service_name=~"gozleme-.+"} |~ "Publisher registered|Capability created|Capability renamed|Operator reserved handle claim|Operator reclaimed handle claim"';
  let audit: Awaited<ReturnType<typeof storedLogs>> = [];
  await expect
    .poll(
      async () => {
        audit = (await storedLogs(auditQuery, 20)).filter((l) => l.at >= suiteStarted);
        return [`Publisher registered ${publisherId} in ${DEMO_ORG.id} by ${aliceId}`, `Capability created ${capability.id} under ${publisherId}`, "Operator reserved handle claim", "Operator reclaimed handle claim"].every((t) =>
          audit.some((l) => l.line.includes(t)),
        );
      },
      { timeout: 90_000, intervals: [3000] },
    )
    .toBe(true);

  const page = await alice.context().newPage();
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`${JAEGER_URL}/trace/${registered.traceId}`);
  await expect(page.getByText("gozleme.publisher.register").first()).toBeAttached({ timeout: 15_000 });
  await page.getByRole("switch", { name: "gozleme-api gozleme.publisher.register" }).first().click();
  await expect(page.getByRole("heading", { name: "gozleme.publisher.register", level: 2 })).toBeVisible();
  const privateValues = [HANDLE, EARMARKED, BLOCKED, SQUATTED, capability.alias, ...DISPLAY, DEMO.alice, DEMO.carol, DEMO.dave, DEMO_PASSWORD, DEMO_ORG.name, "gozleme_session", ...secretStrings];
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/07-publisher-register-trace-jaeger.png`, fullPage: true });
  await page.close();

  const telemetry = JSON.stringify(spans) + metrics + JSON.stringify(audit);
  for (const v of privateValues) expect(telemetry, `telemetry must not contain ${v}`).not.toContain(v);
  expect(telemetry).not.toMatch(TOKEN);
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(suiteStarted).toISOString()], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of [CONTEXT_KEY, HANDLE, EARMARKED, BLOCKED, SQUATTED, capability.alias, ...DISPLAY, DEMO.alice, DEMO.carol]) expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);
  expect(composeLogs).not.toMatch(TOKEN);

  writeEvidence({ ...JSON.parse(readEvidence()), observability: { spans: ["gozleme.publisher.register", "gozleme.capability.create"], counter: "gozleme_publisher_operations_total", auditEvents: audit.length } });
});

function readEvidence() {
  return readFileSync(`${evidence}/publisher-evidence.json`, "utf8");
}

/** Opaque IDs and synthetic handles only: no email, organization name, display name, key or token. */
function writeEvidence(record: unknown) {
  const text = JSON.stringify(record, null, 2);
  for (const v of [CONTEXT_KEY, DEMO.alice, DEMO.carol, DEMO.dave, DEMO_ORG.name, PARTNER_ORG.name, ...DISPLAY, ...secretStrings]) expect(text).not.toContain(v);
  expect(text).not.toMatch(TOKEN);
  writeFileSync(`${evidence}/publisher-evidence.json`, `${text}\n`);
}
