import AxeBuilder from "@axe-core/playwright";
import { createHmac } from "node:crypto";
import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { composeWithEnv, repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, PARTNER_ORG, evidenceDir, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0003 autonomous demo: two seeded tenants (alice in Seeded Demo Org, dave in Seeded Partner
 * Org) each see only their own data in the UI and the API; the other tenant's organization answers
 * exactly like an unknown id; the database itself (as the serving role and as the owner login, with
 * no, forged and signed contexts) returns only the signed account's rows; an API whose key differs
 * from the provisioned one fails closed, counted and audited; the tenant transaction span is traced
 * with no token, key or personal data anywhere in telemetry.
 */
const shots = shotsDir("goz-0003");
const evidence = evidenceDir("goz-0003");
const suiteStarted = Date.now();
const CONTEXT_KEY = process.env.GOZLEME_CONTEXT_KEY ?? "local-dev-context-key-not-a-secret-0001";
const MISMATCHED_KEY = "local-dev-context-key-not-a-secret-0099";
const TOKEN = /v1\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.\d+\.[0-9a-f]{64}/;

let alice: Page;
let dave: Page;

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

/** One psql call inside the postgres container as `role` (local socket); the last row of the last statement. */
function psql(role: string, sql: string): string {
  const out = execFileSync("docker", ["compose", "exec", "-T", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-U", role, "-d", "gozleme", "-c", sql], { cwd: repoRoot }).toString();
  return out.trim().split("\n").pop() ?? "";
}

/** The API's token construction (contracts/tenant-context.md §1), for database-level evidence only. */
function sign(accountId: string, key: string, expires = Math.floor(Date.now() / 1000) + 300) {
  const payload = `v1.${accountId}.${expires}`;
  return `${payload}.${createHmac("sha256", key).update(payload, "utf8").digest("hex")}`;
}

function inContext(token: string | null, count: string) {
  // One simple query is one transaction, so the transaction-local setting holds for the count and then ends.
  const set = token === null ? "" : `DO $$BEGIN PERFORM set_config('gozleme.account_context', '${token}', true); END$$;`;
  return `${set} ${count}`;
}

async function orgIds(page: Page): Promise<string[]> {
  const body = (await (await page.request.get(`${API_URL}/api/v1/organizations`)).json()) as { organizations: { id: string }[] };
  return body.organizations.map((o) => o.id);
}

test.afterAll(async () => {
  // Leave the API with the provisioned key even if the fail-closed step failed midway.
  composeWithEnv({ GOZLEME_CONTEXT_KEY: CONTEXT_KEY }, "up", "-d", "--wait", "--no-deps", "api");
  for (const page of [alice, dave]) await page?.context().close();
});

test("two tenants: each account sees only its own organizations", async ({ browser }) => {
  alice = await signedInPage(browser, DEMO.alice);
  dave = await signedInPage(browser, DEMO.dave);

  await alice.goto("/organizations");
  await expect(alice.getByTestId("organization-list").getByRole("link", { name: DEMO_ORG.name, exact: true })).toBeVisible();
  await expect(alice.locator("main")).not.toContainText(PARTNER_ORG.name);
  await expect(alice.getByTestId("org-switcher").locator("option", { hasText: PARTNER_ORG.name })).toHaveCount(0);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/01-alice-sees-only-her-tenants.png`, fullPage: true });

  await dave.goto("/organizations");
  const daves = dave.getByTestId("organization-list").getByRole("listitem");
  await expect(daves).toHaveCount(1);
  await expect(daves).toContainText(PARTNER_ORG.name);
  await expect(dave.locator("main")).not.toContainText(DEMO_ORG.name);
  await expectAccessible(dave);
  await dave.screenshot({ path: `${shots}/02-dave-sees-only-seeded-partner-org.png`, fullPage: true });

  expect(await orgIds(alice)).toContain(DEMO_ORG.id);
  expect(await orgIds(alice)).not.toContain(PARTNER_ORG.id);
  expect(await orgIds(dave)).toEqual([PARTNER_ORG.id]);

  // SC-005: organization pages still load in under 1 s each locally with row-level security in place.
  for (const [page, id, name] of [[alice, DEMO_ORG.id, DEMO_ORG.name], [dave, PARTNER_ORG.id, PARTNER_ORG.name]] as const) {
    for (const [path, ready] of [["/organizations", page.getByTestId("organization-list")], [`/organizations/${id}`, page.getByTestId("organization-name")]] as const) {
      const started = Date.now();
      await page.goto(path);
      await expect(ready).toContainText(name);
      expect(Date.now() - started, `SC-005: ${path} under 1 s`).toBeLessThan(1000);
    }
  }
});

test("the other tenant's organization answers exactly like an unknown id, in the UI and the API", async () => {
  const snapshot = async (page: Page, path: string, method = "GET") => {
    const res = await page.request.fetch(`${API_URL}${path}`, { method, headers: { "x-gozleme-client": "web" } });
    return `${res.status()}|${await res.text()}|${res.headers()["cache-control"]}`;
  };
  for (const [page, other] of [[dave, DEMO_ORG.id], [alice, PARTNER_ORG.id]] as const) {
    for (const [suffix, method] of [["", "GET"], ["/members", "GET"], ["/invitations", "GET"], ["/select", "POST"], ["/invitations", "POST"]] as const) {
      const real = await snapshot(page, `/api/v1/organizations/${other}${suffix}`, method);
      expect(real, `${method} ${suffix}`).toBe("404||no-store");
      expect(await snapshot(page, `/api/v1/organizations/${crypto.randomUUID()}${suffix}`, method)).toBe(real);
    }
    // Caller-chosen headers never select an account or organization.
    const res = await page.request.get(`${API_URL}/api/v1/organizations/${other}`, { headers: { "x-gozleme-organization": other, "x-gozleme-account": other } });
    expect(res.status()).toBe(404);
  }
  const res = await dave.goto(`/organizations/${DEMO_ORG.id}`);
  expect(res?.status()).toBe(404);
  await expect(dave.getByRole("heading", { name: "Organization not found" })).toBeVisible();
  await expectAccessible(dave);
  await dave.screenshot({ path: `${shots}/03-dave-cannot-reach-seeded-demo-org.png`, fullPage: true });
  expect(await orgIds(dave)).toEqual([PARTNER_ORG.id]);
});

test("database: forced row-level security returns only the signed account's rows, for the serving role and the owner", async () => {
  const id = (email: string) => psql("postgres", `SELECT id FROM gozleme.users WHERE email = '${email}'`);
  const aliceId = id(DEMO.alice);
  const daveId = id(DEMO.dave);
  const count = (table: string) => `SELECT count(*) FROM gozleme.${table}`;
  const truth = Number(psql("postgres", count("organizations")));
  const catalog = psql(
    "postgres",
    `SELECT string_agg(c.relname || ':' || c.relrowsecurity || ':' || c.relforcerowsecurity || ':' || (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid), ',' ORDER BY c.relname)
       FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'gozleme' AND c.relname IN ('organizations','memberships','invitations')`,
  );
  expect(catalog).toBe("invitations:true:true:4,memberships:true:true:4,organizations:true:true:3");

  const rows: Record<string, unknown>[] = [];
  for (const role of ["gozleme_app", "gozleme_migrator"]) {
    const cases: [string, string | null][] = [
      ["no context", null],
      ["plain account id (forged)", aliceId],
      ["signed with the wrong key", sign(aliceId, MISMATCHED_KEY)],
      ["tampered (account swapped, MAC kept)", sign(aliceId, CONTEXT_KEY).replace(aliceId, daveId)],
      ["expired", sign(aliceId, CONTEXT_KEY, Math.floor(Date.now() / 1000) - 5)],
      ["signed for alice", sign(aliceId, CONTEXT_KEY)],
      ["signed for dave", sign(daveId, CONTEXT_KEY)],
    ];
    for (const [label, token] of cases) {
      const organizations = Number(psql(role, inContext(token, count("organizations"))));
      const visible = psql(role, inContext(token, "SELECT coalesce(string_agg(id::text, ',' ORDER BY id), '') FROM gozleme.organizations")).split(",").filter(Boolean);
      const memberships = Number(psql(role, inContext(token, count("memberships"))));
      rows.push({ role, context: label, organizations, memberships, partnerVisible: visible.includes(PARTNER_ORG.id), demoVisible: visible.includes(DEMO_ORG.id) });
      if (label.startsWith("signed for alice")) {
        expect(visible).toContain(DEMO_ORG.id);
        expect(visible).not.toContain(PARTNER_ORG.id);
      } else if (label.startsWith("signed for dave")) {
        expect(visible).toEqual([PARTNER_ORG.id]);
      } else {
        expect(organizations, `${role}: ${label}`).toBe(0);
        expect(memberships, `${role}: ${label}`).toBe(0);
      }
    }
  }
  // The serving role cannot read the key, call the internal verifier or switch RLS off.
  const denied = (sql: string) => {
    try {
      psql("gozleme_app", sql);
      return false;
    } catch {
      return true;
    }
  };
  const refusals = {
    readKey: denied("SELECT key FROM gozleme_private.context_keys"),
    callVerifier: denied("SELECT * FROM gozleme_private.verify_context('x')"),
    disableRls: denied("ALTER TABLE gozleme.organizations DISABLE ROW LEVEL SECURITY"),
    bypassSetting: denied("SET row_security = off; SELECT count(*) FROM gozleme.organizations"),
  };
  expect(refusals).toEqual({ readKey: true, callVerifier: true, disableRls: true, bypassSetting: true });

  const record = { spec: "GOZ-0003", capturedAt: new Date().toISOString(), superuserOrganizationCount: truth, catalog, contexts: rows, appRoleRefusals: refusals };
  const text = JSON.stringify(record, null, 2);
  for (const v of [CONTEXT_KEY, DEMO.alice, DEMO.dave, ...secretStrings]) expect(text).not.toContain(v);
  expect(text).not.toMatch(TOKEN);
  writeFileSync(`${evidence}/rls-evidence.json`, `${text}\n`);
});

test("fail closed: an API whose key differs from the provisioned one shows no organization data", async ({ browser, request }) => {
  test.setTimeout(240_000);
  composeWithEnv({ GOZLEME_CONTEXT_KEY: MISMATCHED_KEY }, "up", "-d", "--wait", "--no-deps", "api");
  try {
    const page = await signedInPage(browser, DEMO.alice); // identity needs no tenant context
    await page.goto("/organizations");
    await expect(page.getByTestId("organizations-empty")).toBeVisible();
    await expect(page.locator("main")).not.toContainText(DEMO_ORG.name);
    expect(await orgIds(page)).toEqual([]);
    const direct = await page.request.get(`${API_URL}/api/v1/organizations/${DEMO_ORG.id}`);
    expect(direct.status()).toBe(404);
    await expectAccessible(page);
    await page.screenshot({ path: `${shots}/04-mismatched-key-fails-closed.png`, fullPage: true });
    // A create passes the application check and is refused by the database: "not found", never created.
    const refusedCreate = await page.evaluate(
      async () => (await fetch("/api/organizations", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ displayName: "Refused under mismatched key" }) })).status,
    );
    expect(refusedCreate).toBe(404);
    await page.context().close();

    await expect
      .poll(async () => (await storedLogs('{service_name="gozleme-api"} |= "self-check failed"', 10)).filter((l) => l.at >= suiteStarted).length, { timeout: 60_000, intervals: [2000] })
      .toBeGreaterThan(0);
    await expect
      .poll(async () => (await storedLogs('{service_name="gozleme-api"} |= "Tenant context rejected (invalid)"', 10)).filter((l) => l.at >= suiteStarted).length, { timeout: 60_000, intervals: [2000] })
      .toBeGreaterThan(0);
    // The SDK exports metrics every 60 s.
    let metrics = "";
    await expect
      .poll(
        async () => {
          metrics = await (await request.get(METRICS_URL)).text();
          const lines = metrics.split("\n");
          return (
            lines.some((l) => l.startsWith("gozleme_tenant_denials_total{") && l.includes('gozleme_tenant_denial_reason="invalid"')) &&
            // The refused create closes its organization span and counter with a bounded category.
            lines.some((l) => l.startsWith("gozleme_org_operations_total{") && l.includes('gozleme_org_operation="create"') && l.includes('gozleme_org_failure_category="refused_write"'))
          );
        },
        { timeout: 120_000, intervals: [5000] },
      )
      .toBe(true);
    const reasons = metrics.split("\n").filter((l) => l.startsWith("gozleme_tenant_denials_total{")).map((l) => /gozleme_tenant_denial_reason="([^"]+)"/.exec(l)?.[1]);
    for (const r of reasons) expect(["missing", "invalid", "expired", "refused_write"]).toContain(r);
    for (const v of [CONTEXT_KEY, MISMATCHED_KEY, DEMO.alice, DEMO_ORG.name]) expect(metrics).not.toContain(v);
  } finally {
    composeWithEnv({ GOZLEME_CONTEXT_KEY: CONTEXT_KEY }, "up", "-d", "--wait", "--no-deps", "api");
  }
  // Restored: alice's data is back on the next request.
  const restored = await signedInPage(browser, DEMO.alice);
  expect(await orgIds(restored)).toContain(DEMO_ORG.id);
  await restored.context().close();
});

test("observability: each organization request runs in one traced tenant transaction with no token, key or personal data", async () => {
  test.setTimeout(120_000);
  const fresh = await signedInPage(dave.context().browser()!, DEMO.dave);
  await fresh.goto(`/organizations/${PARTNER_ORG.id}`);
  await expect(fresh.getByTestId("organization-name")).toHaveText(PARTNER_ORG.name);
  // A successful organization operation, so the screenshot shows an established context, not a denial.
  const selected = await fresh.evaluate(async (id) => (await fetch(`/api/organizations/${id}/select`, { method: "POST" })).status, PARTNER_ORG.id);
  expect(selected).toBe(204);
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  let traceId: string | undefined;
  await expect
    .poll(
      async () => {
        spans = await recentSpans("gozleme-api", 10, "gozleme.org.select");
        const ok = spans.find((s) => s.name === "gozleme.org.select" && s.attributes["gozleme.org.outcome"] === "success" && s.attributes["gozleme.organization.id"] === PARTNER_ORG.id);
        traceId = ok?.traceId;
        const trace = spans.filter((s) => s.traceId === traceId);
        return trace.some((s) => s.name === "gozleme.tenant.transaction") && trace.some((s) => s.attributes["db.system.name"] === "postgresql");
      },
      { timeout: 45_000, intervals: [1500] },
    )
    .toBe(true);
  for (const s of spans.filter((s) => s.name === "gozleme.tenant.transaction")) expect(s.attributes["gozleme.organization.id"]).toBeUndefined();

  const privateValues = [DEMO.alice, DEMO.dave, DEMO_PASSWORD, CONTEXT_KEY, MISMATCHED_KEY, DEMO_ORG.name, PARTNER_ORG.name, "gozleme_session", ...secretStrings];
  const telemetry = JSON.stringify(spans);
  for (const v of privateValues) expect(telemetry, `traces must not contain ${v}`).not.toContain(v);
  expect(telemetry).not.toMatch(TOKEN);

  const page = await fresh.context().newPage();
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(`${JAEGER_URL}/trace/${traceId}`);
  await expect(page.getByText("gozleme.tenant.transaction").first()).toBeAttached({ timeout: 15_000 });
  await expect(page.getByText("gozleme.org.select").first()).toBeAttached();
  // Open the tenant span: its details show the name and, expanded by default, the established/cleared events.
  await page.getByRole("switch", { name: "gozleme-api gozleme.tenant.transaction" }).first().click();
  await expect(page.getByRole("heading", { name: "gozleme.tenant.transaction", level: 2 })).toBeVisible();
  await expect(page.getByRole("switch", { name: /^gozleme\.tenant\.context\.established .*result=ok$/ })).toBeVisible();
  await expect(page.getByRole("switch", { name: /^gozleme\.tenant\.context\.cleared .*outcome=commit$/ })).toBeVisible();
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/05-tenant-transaction-trace-jaeger.png`, fullPage: true });
  await page.close();
  await fresh.context().close();

  const stored = JSON.stringify(await storedLogs('{service_name=~"gozleme-.*"}', 15));
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color"], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of [CONTEXT_KEY, MISMATCHED_KEY, DEMO.alice, DEMO.dave, PARTNER_ORG.name]) {
    expect(stored, `stored logs must not contain ${v}`).not.toContain(v);
    expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);
  }
  expect(stored).not.toMatch(TOKEN);
  expect(composeLogs).not.toMatch(TOKEN);
});
