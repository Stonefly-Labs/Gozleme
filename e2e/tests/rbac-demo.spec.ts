import AxeBuilder from "@axe-core/playwright";
import { createHmac } from "node:crypto";
import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, evidenceDir, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0004 autonomous demo: in Seeded Demo Org, carol (member) is denied a rename in the UI and the
 * API; alice grants her admin in the UI and carol's next rename succeeds; alice revokes it and carol's
 * stale page gets the permission message; the last admin cannot demote itself; demotion revokes the
 * demoted account's invitations; a non-member cannot tell the roles endpoint from an unknown one. The
 * API role × action matrix, the policy-evaluator boundary and the database's own refusals are recorded
 * as evidence; every decision is traced and counted with no personal data.
 */
const shots = shotsDir("goz-0004");
const evidence = evidenceDir("goz-0004");
const suiteStarted = Date.now();
const CONTEXT_KEY = process.env.GOZLEME_CONTEXT_KEY ?? "local-dev-context-key-not-a-secret-0001";
const TOKEN = /v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.\d+\.[0-9a-f]{64}/;
const FORBIDDEN = "You no longer have permission to do that.";
const LAST_ADMIN = "An organization needs at least one admin.";
const RENAMED = `${DEMO_ORG.name} (renamed by carol)`;
const ORG_API = `${API_URL}/api/v1/organizations/${DEMO_ORG.id}`;

let alice: Page;
let carol: Page;
let dave: Page;
let carolId: string;
let aliceId: string;
const codes: string[] = [];

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

/** A direct API call with the web-client header (CSRF layer 2), as a tampered or scripted client would send it. */
async function api(page: Page, method: string, path: string, data?: unknown) {
  const res = await page.request.fetch(path.startsWith("http") ? path : `${ORG_API}${path}`, {
    method,
    headers: { "x-gozleme-client": "web", "content-type": "application/json" },
    data: data === undefined ? undefined : JSON.stringify(data),
  });
  const text = await res.text();
  return { status: res.status(), text, json: text ? (JSON.parse(text) as Record<string, unknown>) : null, cache: res.headers()["cache-control"] };
}

const setRoles = (page: Page, userId: string, roles: string[]) => api(page, "PUT", `/members/${userId}/roles`, { roles });
const displayName = async (page: Page) => (await api(page, "GET", "")).json?.displayName;

/** One psql call inside the postgres container as `role`; SQLSTATE in errors (VERBOSITY verbose). */
function psql(role: string, sql: string): { out: string; state?: string } {
  try {
    const out = execFileSync("docker", ["compose", "exec", "-T", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-v", "VERBOSITY=verbose", "-U", role, "-d", "gozleme", "-c", sql], {
      cwd: repoRoot,
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

/** `statement` as gozleme_app in `account`'s signed context, always rolled back; returns rows affected or the SQLSTATE. */
function asApp(account: string, statement: string): string {
  const sql = `BEGIN; DO $$BEGIN PERFORM set_config('gozleme.account_context', '${sign(account)}', true); END$$; WITH c AS (${statement} RETURNING 1) SELECT count(*) FROM c; ROLLBACK;`;
  const { out, state } = psql("gozleme_app", sql);
  return state ?? `${out} row(s)`;
}

test.afterAll(async () => {
  // Restore the seeded roles and name even if a step failed midway.
  if (alice && carolId) {
    await setRoles(alice, carolId, ["member"]);
    await api(alice, "PATCH", "", { displayName: DEMO_ORG.name });
  }
  for (const page of [alice, carol, dave]) await page?.context().close();
});

test("a member sees role badges but no admin controls, and the API refuses a member's rename", async ({ browser }) => {
  alice = await signedInPage(browser, DEMO.alice);
  carol = await signedInPage(browser, DEMO.carol);
  dave = await signedInPage(browser, DEMO.dave);
  aliceId = (await (await alice.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId;
  carolId = (await (await carol.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId;
  expect((await setRoles(alice, carolId, [])).status).toBe(200); // the seeded state: carol is a member only

  await carol.goto(`/organizations/${DEMO_ORG.id}`);
  await expect(carol.getByTestId("organization-name")).toHaveText(DEMO_ORG.name);
  await expect(carol.getByRole("list", { name: `Roles of ${DEMO.carol}` }).getByRole("listitem")).toHaveText(["member"]);
  await expect(carol.getByRole("list", { name: `Roles of ${DEMO.alice}` }).getByRole("listitem")).toHaveText(["admin", "member"]);
  await expect(carol.getByLabel("Organization name")).toHaveCount(0);
  await expect(carol.getByRole("heading", { name: "Invitations", exact: true })).toHaveCount(0);
  await expect(carol.getByRole("button", { name: /^Remove / })).toHaveCount(0);
  await expect(carol.getByRole("button", { name: /^Edit roles/ })).toHaveCount(0);
  await expect(carol.getByRole("button", { name: "Leave organization" })).toBeVisible();
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/01-member-sees-no-admin-controls.png`, fullPage: true });

  const denied = await api(carol, "PATCH", "", { displayName: "Carol was here" });
  expect(denied.status).toBe(403);
  expect(denied.json).toEqual({ error: "forbidden" });
  expect(await displayName(alice)).toBe(DEMO_ORG.name);
});

test("an admin grants admin in the UI and the member's next rename succeeds", async () => {
  await alice.goto(`/organizations/${DEMO_ORG.id}`);
  await alice.getByRole("button", { name: `Edit roles of ${DEMO.carol}` }).click();
  const editor = alice.getByRole("form", { name: `Edit roles of ${DEMO.carol}` });
  await editor.getByRole("checkbox", { name: "admin" }).check();
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/02-admin-edits-carols-roles.png`, fullPage: true });
  await editor.getByRole("button", { name: "Save roles" }).click();
  await expect(alice.getByRole("list", { name: `Roles of ${DEMO.carol}` }).getByRole("listitem")).toHaveText(["admin", "member"]);
  await alice.screenshot({ path: `${shots}/03-carol-granted-admin.png`, fullPage: true });

  // carol is still signed in; her next request carries the new permission.
  await carol.goto(`/organizations/${DEMO_ORG.id}`);
  await carol.getByLabel("Organization name").fill(RENAMED);
  await carol.getByRole("button", { name: "Rename" }).click();
  await expect(carol.getByTestId("organization-name")).toHaveText(RENAMED);
  await expect(carol.getByRole("heading", { name: "Invitations", exact: true })).toBeVisible();
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/04-carol-renames-after-grant.png`, fullPage: true });
  expect((await api(carol, "PATCH", "", { displayName: DEMO_ORG.name })).status).toBe(200);
});

test("revoking admin takes effect on the next request; a stale page shows the permission message", async () => {
  await alice.goto(`/organizations/${DEMO_ORG.id}`);
  await alice.getByRole("button", { name: `Edit roles of ${DEMO.carol}` }).click();
  const editor = alice.getByRole("form", { name: `Edit roles of ${DEMO.carol}` });
  await editor.getByRole("checkbox", { name: "admin" }).uncheck();
  await editor.getByRole("button", { name: "Save roles" }).click();
  await expect(alice.getByRole("list", { name: `Roles of ${DEMO.carol}` }).getByRole("listitem")).toHaveText(["member"]);

  // carol's page still shows the rename form from before the revocation.
  await carol.getByLabel("Organization name").fill("Denied after revoke");
  await carol.getByRole("button", { name: "Rename" }).click();
  await expect(carol.getByText(FORBIDDEN)).toBeVisible();
  await carol.screenshot({ path: `${shots}/05-revoked-member-sees-permission-message.png`, fullPage: true });
  await carol.reload();
  await expect(carol.getByLabel("Organization name")).toHaveCount(0); // the next load drops the form
  expect(await displayName(alice)).toBe(DEMO_ORG.name);
  expect((await carol.request.get(`${API_URL}/api/v1/auth/session`)).status()).toBe(200);
});

test("the last admin cannot demote itself", async () => {
  await alice.goto(`/organizations/${DEMO_ORG.id}`);
  await alice.getByRole("button", { name: `Edit roles of ${DEMO.alice}` }).click();
  const editor = alice.getByRole("form", { name: `Edit roles of ${DEMO.alice}` });
  await editor.getByRole("checkbox", { name: "admin" }).uncheck();
  await editor.getByRole("button", { name: "Save roles" }).click();
  await expect(alice.getByTestId("member-error")).toHaveText(LAST_ADMIN);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/06-last-admin-cannot-demote-itself.png`, fullPage: true });
  const refused = await setRoles(alice, aliceId, []);
  expect(refused.status).toBe(409);
  expect(refused.json).toEqual({ error: "last_admin" });
  expect((await api(alice, "DELETE", `/members/${aliceId}`)).json).toEqual({ error: "last_admin" });
});

test("demotion revokes the demoted account's invitations; a non-member gets an identical 404", async () => {
  expect((await setRoles(alice, carolId, ["admin"])).status).toBe(200);
  const created = await api(carol, "POST", "/invitations");
  expect(created.status).toBe(201);
  const code = String(created.json!.code);
  codes.push(code);
  expect((await setRoles(alice, carolId, [])).status).toBe(200);
  const open = await api(alice, "GET", "/invitations");
  expect((open.json!.invitations as { id: string }[]).map((i) => i.id)).not.toContain(created.json!.id);
  const redeem = await api(dave, "POST", `${API_URL}/api/v1/invitations/redeem`, { code });
  expect(redeem.status).toBe(400);
  expect(redeem.json).toEqual({ error: "invalid_invitation" });

  const real = await setRoles(dave, carolId, ["admin"]);
  const unknown = await api(dave, "PUT", `${API_URL}/api/v1/organizations/${crypto.randomUUID()}/members/${carolId}/roles`, { roles: ["admin"] });
  expect(`${real.status}|${real.text}|${real.cache}`).toBe("404||no-store");
  expect(`${unknown.status}|${unknown.text}|${unknown.cache}`).toBe(`${real.status}|${real.text}|${real.cache}`);
});

test("evidence: API role × action matrix, policy-evaluator boundary and database refusals", async () => {
  test.setTimeout(120_000);
  const matrix: Record<string, Record<string, string>> = {};
  for (const role of ["", "publisher", "reviewer", "approver", "admin"]) {
    expect((await setRoles(alice, carolId, role ? [role] : [])).status).toBe(200);
    const detail = await api(carol, "GET", "");
    const permitted = (detail.json!.permissions as string[]) ?? [];
    const pending = await api(alice, "POST", "/invitations");
    const calls: [string, () => ReturnType<typeof api>][] = [
      ["organization.read", () => api(carol, "GET", "")],
      ["organization.select", () => api(carol, "POST", "/select")],
      ["organization.rename", () => api(carol, "PATCH", "", { displayName: DEMO_ORG.name })],
      ["membership.list", () => api(carol, "GET", "/members")],
      ["membership.roles.change", () => setRoles(carol, carolId, role ? [role] : [])],
      ["membership.remove", () => api(carol, "DELETE", `/members/${crypto.randomUUID()}`)], // authorized before the (unknown) target is looked up
      ["invitation.list", () => api(carol, "GET", "/invitations")],
      ["invitation.create", () => api(carol, "POST", "/invitations")],
      ["invitation.revoke", () => api(carol, "DELETE", `/invitations/${pending.json!.id}`)],
    ];
    const row: Record<string, string> = {};
    for (const [action, call] of calls) {
      const { status, json } = await call();
      const outcome = status === 403 ? "deny" : "allow";
      expect(outcome, `${role || "member"} ${action} (${status})`).toBe(permitted.includes(action) ? "allow" : "deny");
      if (action === "invitation.create" && status === 201) await api(alice, "DELETE", `/invitations/${json!.id}`);
      row[action] = `${outcome} (${status})`;
    }
    row["membership.leave"] = "allow (covered destructively by RoleAssignmentTests)";
    await api(alice, "DELETE", `/invitations/${pending.json!.id}`);
    matrix[role ? `member+${role}` : "member"] = row;
  }
  expect((await setRoles(alice, carolId, [])).status).toBe(200);

  const boundary: Record<string, number> = {};
  for (const [method, suffix] of [
    ["GET", "/policies"],
    ["POST", "/policies"],
    ["GET", "/roles"],
    ["POST", "/roles"],
    ["GET", "/permissions"],
    ["POST", "/grants"],
    ["POST", `/members/${carolId}/roles`],
    ["PUT", `/members/${carolId}/role`],
    ["POST", "/transfer"],
  ] as const) {
    const { status } = await api(alice, method, suffix, method === "GET" ? undefined : { role: "admin" });
    expect([404, 405], `${method} ${suffix}`).toContain(status);
    boundary[`${method} ${suffix.replace(carolId, "{userId}")}`] = status;
  }
  const custom = await setRoles(alice, carolId, ["auditor"]);
  expect(custom.status).toBe(400);
  boundary["PUT /members/{userId}/roles [auditor]"] = custom.status;

  const carolRow = `WHERE organization_id = '${DEMO_ORG.id}' AND user_id = '${carolId}'`;
  const database = {
    "member raises own roles": asApp(carolId, `UPDATE gozleme.memberships SET roles = '{admin,member}' ${carolRow}`),
    "member renames the organization": asApp(carolId, `UPDATE gozleme.organizations SET display_name = 'x' WHERE id = '${DEMO_ORG.id}'`),
    "member removes the admin": asApp(carolId, `DELETE FROM gozleme.memberships WHERE organization_id = '${DEMO_ORG.id}' AND user_id = '${aliceId}'`),
    "admin changes a member's roles": asApp(aliceId, `UPDATE gozleme.memberships SET roles = '{member,reviewer}' ${carolRow}`),
    "last admin demotes itself": asApp(aliceId, `UPDATE gozleme.memberships SET roles = '{member}' WHERE organization_id = '${DEMO_ORG.id}' AND user_id = '${aliceId}'`),
  };
  expect(database).toEqual({
    "member raises own roles": "42501",
    "member renames the organization": "0 row(s)",
    "member removes the admin": "42501",
    "admin changes a member's roles": "1 row(s)",
    "last admin demotes itself": "42501",
  });
  expect(psql("postgres", `SELECT roles::text FROM gozleme.memberships ${carolRow}`).out).toBe("{member}"); // all rolled back

  const record = { spec: "GOZ-0004", capturedAt: new Date().toISOString(), organization: DEMO_ORG.id, apiMatrixForCarol: matrix, policyEvaluatorBoundary: boundary, databaseAsAppRole: database };
  const text = JSON.stringify(record, null, 2);
  for (const v of [CONTEXT_KEY, DEMO.alice, DEMO.carol, DEMO.dave, DEMO_ORG.name, ...codes, ...secretStrings]) expect(text).not.toContain(v);
  expect(text).not.toMatch(TOKEN);
  writeFileSync(`${evidence}/rbac-evidence.json`, `${text}\n`);
});

test("observability: authorization decisions are traced, counted and audited with no personal data", async ({ request }) => {
  test.setTimeout(180_000);
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = await recentSpans("gozleme-api", 10, "gozleme.authz.decision");
        const decisions = spans.filter((s) => s.name === "gozleme.authz.decision");
        return (["allow", "deny"] as const).every((o) =>
          decisions.some((s) => s.attributes["gozleme.authz.outcome"] === o && s.attributes["gozleme.authz.action"] === "organization.rename" && s.attributes["gozleme.authz.resource_type"] === "organization"),
        );
      },
      { timeout: 45_000, intervals: [1500] },
    )
    .toBe(true);
  for (const s of spans.filter((s) => s.name === "gozleme.authz.decision")) {
    expect(Object.keys(s.attributes).filter((k) => k.startsWith("gozleme."))).toEqual(expect.arrayContaining(["gozleme.authz.action", "gozleme.authz.resource_type", "gozleme.authz.outcome"]));
    expect(Object.values(s.attributes)).not.toContain(carolId);
  }

  let metrics = "";
  await expect
    .poll(
      async () => {
        metrics = await (await request.get(METRICS_URL)).text();
        const lines = metrics.split("\n");
        return (
          lines.some((l) => l.startsWith("gozleme_authz_decisions_total{") && l.includes('gozleme_authz_outcome="deny"')) &&
          lines.some((l) => l.startsWith("gozleme_org_operations_total{") && l.includes('gozleme_org_failure_category="last_admin"'))
        );
      },
      { timeout: 90_000, intervals: [5000] },
    )
    .toBe(true);
  for (const line of metrics.split("\n").filter((l) => l.startsWith("gozleme_authz_decisions_total{"))) {
    const labels = [...line.matchAll(/(\w+)="/g)].map((m) => m[1]).filter((k) => k.startsWith("gozleme_"));
    expect(labels.sort()).toEqual(["gozleme_authz_action", "gozleme_authz_outcome", "gozleme_authz_resource_type"]);
  }

  await expect
    .poll(async () => (await storedLogs('{service_name="gozleme-api"} |~ "Authorization denied|roles changed from"', 15)).filter((l) => l.at >= suiteStarted).length, { timeout: 60_000, intervals: [2000] })
    .toBeGreaterThan(1);
  const audit = (await storedLogs('{service_name="gozleme-api"} |~ "Authorization denied|roles changed from"', 15)).filter((l) => l.at >= suiteStarted);
  expect(audit.some((l) => l.line.includes(`Authorization denied (organization.rename) in ${DEMO_ORG.id} ${carolId}`))).toBe(true);
  expect(audit.some((l) => l.line.includes(`Member ${carolId} roles changed from member to admin,member in ${DEMO_ORG.id} by ${aliceId}`))).toBe(true);

  const deny = spans.find((s) => s.name === "gozleme.authz.decision" && s.attributes["gozleme.authz.outcome"] === "deny" && s.attributes["gozleme.authz.action"] === "organization.rename")!;
  const page = await alice.context().newPage();
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`${JAEGER_URL}/trace/${deny.traceId}`);
  await expect(page.getByText("gozleme.authz.decision").first()).toBeAttached({ timeout: 15_000 });
  await page.getByRole("switch", { name: "gozleme-api gozleme.authz.decision" }).first().click();
  await expect(page.getByRole("heading", { name: "gozleme.authz.decision", level: 2 })).toBeVisible();
  const privateValues = [DEMO.alice, DEMO.carol, DEMO.dave, DEMO_PASSWORD, DEMO_ORG.name, RENAMED, ...codes, "gozleme_session", ...secretStrings];
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/07-authz-decision-trace-jaeger.png`, fullPage: true });
  await page.close();

  const telemetry = JSON.stringify(spans) + metrics + JSON.stringify(audit);
  for (const v of privateValues) expect(telemetry, `telemetry must not contain ${v}`).not.toContain(v);
  expect(telemetry).not.toMatch(TOKEN);
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color", "--since", new Date(suiteStarted).toISOString()], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of [CONTEXT_KEY, DEMO.alice, DEMO.carol, DEMO_ORG.name, RENAMED, ...codes]) expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);
  expect(composeLogs).not.toMatch(TOKEN);
});
