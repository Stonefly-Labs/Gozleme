import AxeBuilder from "@axe-core/playwright";
import { execFileSync } from "node:child_process";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL, WEB_URL } from "../playwright.config";
import { repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_ORG, DEMO_PASSWORD, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0002 autonomous demo: create two organizations, switch context, rename; invite with a
 * single-use code, redeem, refuse reuse and revoked codes; remove a member, who loses access on the
 * next request without being signed out; the last-member guard; existence-hiding 404s and cross-site
 * refusals; the no-delegation boundary; one traced flow with no personal data in telemetry.
 * Accounts are the seeded synthetic `*.test` ones (alice, bob, carol); every sign-in is fresh
 * (GOZ-0001-IF-09). Invitation codes are secrets: every screenshot that could show one masks it.
 */
const shots = shotsDir("goz-0002");
const suiteStarted = Date.now();
const run = String(Date.now()).slice(-6);
const ALPHA = `Demo Org Alpha ${run}`;
const BETA = `Demo Org Beta ${run}`;
const BETA_RENAMED = `Demo Org Beta Renamed ${run}`;
const SOLO = `Demo Org Solo ${run}`;
const INVALID = "This invitation is invalid or has expired.";
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const state: { alpha?: string; beta?: string; code?: string; revokedCode?: string; createMs?: number[] } = {};
let alice: Page;
let bob: Page;
let carol: Page;

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

/** An organization's id from its link in the list (opaque; never parsed by the UI). */
async function orgIdFromList(page: Page, name: string) {
  const href = await page.getByTestId("organization-list").getByRole("link", { name, exact: true }).getAttribute("href");
  return decodeURIComponent(href!.split("/").pop()!);
}

async function createInvitation(page: Page) {
  await page.getByRole("button", { name: "Create invitation" }).click();
  const field = page.getByTestId("invitation-code");
  await expect(field).toHaveValue(/^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$/);
  return field.inputValue();
}

async function redeem(page: Page, code: string) {
  await page.goto("/join");
  await page.getByLabel("Invitation code").fill(code);
  await page.getByRole("button", { name: "Join organization" }).click();
}

/** Same-origin POST from inside the page, so the browser sends `Sec-Fetch-Site: same-origin` like the UI. */
async function browserFetch(page: Page, method: string, path: string, body?: unknown) {
  return page.evaluate(
    async ([m, p, b]) => {
      const res = await fetch(p, { method: m, headers: b === undefined ? undefined : { "content-type": "application/json" }, body: b === undefined ? undefined : JSON.stringify(b) });
      return { status: res.status, body: await res.text() };
    },
    [method, path, body] as const,
  );
}

test.afterAll(async () => {
  for (const page of [alice, bob, carol]) await page?.context().close();
});

test("anonymous: organization pages redirect to sign-in and the API refuses", async ({ page, request }) => {
  for (const [path, returnTo] of [["/organizations", "%2Forganizations"], ["/join", "%2Fjoin"], [`/organizations/${DEMO_ORG.id}`, encodeURIComponent(`/organizations/${DEMO_ORG.id}`)]]) {
    await page.goto(path);
    await expect(page).toHaveURL(`${WEB_URL}/login?returnTo=${returnTo}`);
  }
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.screenshot({ path: `${shots}/01-anonymous-redirected-to-sign-in.png`, fullPage: true });

  for (const [method, path] of [
    ["GET", "/api/v1/organizations"],
    ["POST", "/api/v1/organizations"],
    ["GET", `/api/v1/organizations/${DEMO_ORG.id}/members`],
    ["POST", "/api/v1/invitations/redeem"],
  ] as const) {
    const res = await request.fetch(`${API_URL}${path}`, { method, headers: { "x-gozleme-client": "web", "content-type": "application/json" }, data: method === "POST" ? {} : undefined });
    expect(res.status(), `${method} ${path}`).toBe(401);
  }
  expect((await request.get("/api/organizations")).status()).toBe(401);
});

test("create two organizations, see only your own, switch context and rename", async ({ browser }) => {
  alice = await signedInPage(browser, DEMO.alice);
  await alice.getByRole("banner").getByRole("link", { name: "Organizations" }).click();
  await expect(alice.getByRole("heading", { name: "Organizations", level: 1 })).toBeVisible();
  await expect(alice.getByTestId("organization-list").getByRole("link", { name: DEMO_ORG.name })).toBeVisible();

  state.createMs = [];
  for (const name of [ALPHA, BETA]) {
    await alice.goto("/organizations");
    await alice.getByLabel("Organization name").fill(name);
    const started = Date.now();
    await alice.getByRole("button", { name: "Create organization" }).click();
    await expect(alice.getByTestId("organization-name")).toHaveText(name);
    state.createMs.push(Date.now() - started);
    // A new organization becomes the active context.
    await expect(alice.getByTestId("org-switcher").locator("option:checked")).toHaveText(name);
  }
  for (const ms of state.createMs) expect(ms, "SC-001: each action under 1 s locally").toBeLessThan(1000);

  await alice.goto("/organizations");
  state.alpha = await orgIdFromList(alice, ALPHA);
  state.beta = await orgIdFromList(alice, BETA);
  expect(state.alpha).toMatch(UUID);
  expect(state.beta).toMatch(UUID);
  const options = alice.getByTestId("org-switcher").locator("option");
  await expect(options).toContainText([ALPHA, BETA, DEMO_ORG.name]);
  await expect(alice.getByTestId("organization-list").getByRole("listitem").filter({ hasText: BETA }).getByTestId("active-badge")).toBeVisible();
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/02-two-organizations-created.png`, fullPage: true });

  // Switch with the header switcher; the choice survives a reload (stored preference, not session state).
  await alice.getByLabel("Active organization").selectOption({ label: ALPHA });
  await expect(alice).toHaveURL(`${WEB_URL}/organizations/${state.alpha}`);
  await expect(alice.getByTestId("organization-name")).toHaveText(ALPHA);
  await alice.reload();
  await expect(alice.getByTestId("org-switcher").locator("option:checked")).toHaveText(ALPHA);
  const list = await (await alice.request.get(`${API_URL}/api/v1/organizations`)).json();
  expect(list.activeOrganizationId).toBe(state.alpha);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/03-switched-to-alpha.png`, fullPage: true });

  await alice.goto(`/organizations/${state.beta}`);
  await alice.getByLabel("Organization name").fill(BETA_RENAMED);
  await alice.getByRole("button", { name: "Rename" }).click();
  await expect(alice.getByTestId("organization-name")).toHaveText(BETA_RENAMED);
  await expect(alice).toHaveURL(`${WEB_URL}/organizations/${state.beta}`); // the id never changes
  await alice.screenshot({ path: `${shots}/04-renamed-beta.png`, fullPage: true });
});

test("invite with a single-use code shown once; another account joins", async ({ browser }) => {
  await alice.goto(`/organizations/${state.alpha}`);
  state.code = await createInvitation(alice);
  await expect(alice.getByText("shown once")).toBeVisible();
  await alice.screenshot({ path: `${shots}/05-invitation-code-shown-once-masked.png`, fullPage: true, mask: [alice.getByTestId("invitation-code")] });

  await alice.reload();
  await expect(alice.getByTestId("invitation-code")).toHaveCount(0);
  await expect(alice.getByTestId("invitation-row")).toHaveCount(1);
  await expect(alice.locator("body")).not.toContainText(state.code);
  const listed = await (await alice.request.get(`${API_URL}/api/v1/organizations/${state.alpha}/invitations`)).text();
  expect(listed).not.toContain(state.code);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/06-open-invitation-listed-without-code.png`, fullPage: true });

  bob = await signedInPage(browser, DEMO.bob);
  await redeem(bob, state.code.toLowerCase());
  await expect(bob).toHaveURL(`${WEB_URL}/organizations/${state.alpha}`);
  await expect(bob.getByTestId("organization-name")).toHaveText(ALPHA);
  await expect(bob.getByTestId("member-row")).toHaveCount(2);
  await expect(bob.getByTestId("org-switcher").locator("option")).toHaveText([ALPHA]);
  await expectAccessible(bob);
  await bob.screenshot({ path: `${shots}/07-bob-joined-alpha.png`, fullPage: true });
});

test("used and revoked codes are refused with one generic message", async ({ browser }) => {
  carol = await signedInPage(browser, DEMO.carol);
  await redeem(carol, state.code!);
  await expect(carol.getByTestId("redeem-error")).toHaveText(INVALID);
  await expectAccessible(carol);
  await carol.screenshot({ path: `${shots}/08-reused-code-generic-refusal.png`, fullPage: true, mask: [carol.getByLabel("Invitation code")] });

  await alice.goto(`/organizations/${state.alpha}`);
  state.revokedCode = await createInvitation(alice);
  await alice.reload();
  await alice.getByRole("button", { name: /^Revoke invitation/ }).click();
  await expect(alice.getByTestId("no-invitations")).toBeVisible();
  await redeem(carol, state.revokedCode);
  await expect(carol.getByTestId("redeem-error")).toHaveText(INVALID);

  // Byte-identical API refusals for used, revoked and never-issued codes (FR-010).
  const bodies = [];
  for (const code of [state.code!, state.revokedCode, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ"]) {
    const res = await browserFetch(carol, "POST", "/api/invitations/redeem", { code });
    bodies.push(`${res.status} ${res.body}`);
  }
  expect(new Set(bodies)).toEqual(new Set(['400 {"error":"invalid_invitation"}']));
  await carol.goto("/organizations");
  await expect(carol.getByTestId("organization-list").getByRole("link", { name: DEMO_ORG.name })).toBeVisible();
  await expect(carol.getByTestId("organization-list")).not.toContainText(ALPHA);
});

test("removing a member takes effect on their next request; they stay signed in", async () => {
  await bob.goto(`/organizations/${state.alpha}`);
  await expect(bob.getByTestId("organization-name")).toHaveText(ALPHA);

  await alice.goto(`/organizations/${state.alpha}`);
  await alice.getByRole("button", { name: `Remove ${DEMO.bob}` }).click();
  await alice.getByRole("button", { name: "Confirm remove" }).click();
  await expect(alice.getByTestId("member-row")).toHaveCount(1);
  await expectAccessible(alice);
  await alice.screenshot({ path: `${shots}/09-bob-removed-by-alice.png`, fullPage: true });

  // Bob's already-open page: the next navigation is refused, his session is untouched.
  const res = await bob.reload();
  expect(res?.status()).toBe(404);
  await expect(bob.getByRole("heading", { name: "Organization not found" })).toBeVisible();
  expect((await bob.request.get(`${API_URL}/api/v1/auth/session`)).status()).toBe(200);
  await expectAccessible(bob);
  await bob.screenshot({ path: `${shots}/10-removed-member-sees-not-found.png`, fullPage: true });
  await bob.goto("/organizations");
  await expect(bob.getByTestId("account-email")).toHaveText(DEMO.bob); // still signed in
  await expect(bob.locator("main")).not.toContainText(ALPHA);
  await expect(bob.getByTestId("org-switcher").locator("option", { hasText: ALPHA })).toHaveCount(0);
});

test("the last member can neither leave nor be removed", async () => {
  await alice.goto("/organizations");
  await alice.getByLabel("Organization name").fill(SOLO);
  await alice.getByRole("button", { name: "Create organization" }).click();
  await expect(alice.getByTestId("organization-name")).toHaveText(SOLO);
  await alice.getByRole("button", { name: "Leave organization" }).click();
  await alice.getByRole("button", { name: "Confirm leave" }).click();
  await expect(alice.getByTestId("member-error")).toHaveText("An organization must keep at least one member.");
  await expect(alice.getByTestId("member-row")).toHaveCount(1);
  await alice.screenshot({ path: `${shots}/11-last-member-cannot-leave.png`, fullPage: true });
});

test("isolation: a non-member cannot tell real organizations from unknown or malformed ids; cross-site writes fail", async () => {
  const snapshot = async (path: string, method = "GET") => {
    const res = await bob.request.fetch(`${API_URL}${path}`, { method, headers: { "x-gozleme-client": "web" } });
    return `${res.status()}|${await res.text()}|${res.headers()["cache-control"]}`;
  };
  for (const [suffix, method] of [["", "GET"], ["/members", "GET"], ["/invitations", "GET"], ["/select", "POST"], ["/invitations", "POST"]] as const) {
    const real = await snapshot(`/api/v1/organizations/${state.alpha}${suffix}`, method);
    expect(real, `${method} ${suffix}`).toBe("404||no-store");
    expect(await snapshot(`/api/v1/organizations/${crypto.randomUUID()}${suffix}`, method)).toBe(real);
    expect(await snapshot(`/api/v1/organizations/not-a-uuid${suffix}`, method)).toBe(real);
  }
  // The web page answers the same way for a real organization and a malformed id.
  for (const id of [state.alpha!, "not-a-uuid", crypto.randomUUID()]) {
    const res = await bob.goto(`/organizations/${id}`);
    expect(res?.status(), id).toBe(404);
    await expect(bob.getByRole("heading", { name: "Organization not found" })).toBeVisible();
  }

  // A tampered "select" of someone else's organization never becomes bob's context.
  expect((await browserFetch(bob, "POST", `/api/organizations/${state.alpha}/select`)).status).toBe(404);
  expect((await (await bob.request.get(`${API_URL}/api/v1/organizations`)).json()).activeOrganizationId).not.toBe(state.alpha);

  // Cross-site: the web tier refuses foreign origins; the API refuses calls without the web-client header.
  const crossSite = await bob.request.post("/api/organizations", { headers: { origin: "https://attacker.example", "content-type": "application/json" }, data: { displayName: "x" } });
  expect(crossSite.status()).toBe(403);
  const noHeader = await bob.request.post(`${API_URL}/api/v1/organizations`, { headers: { "content-type": "application/json" }, data: { displayName: "x" } });
  expect(noHeader.status()).toBe(403);
  const bobs = (await (await bob.request.get(`${API_URL}/api/v1/organizations`)).json()) as { organizations: { id: string; displayName: string }[] };
  expect(bobs.organizations.map((o) => o.id)).not.toContain(state.alpha);
  expect(bobs.organizations.map((o) => o.displayName)).not.toContain("x");
});

test("boundary: no administrative delegation exists in the UI or the API", async () => {
  await alice.goto(`/organizations/${state.alpha}`);
  await expect(alice.locator("main")).not.toContainText(/\b(role|admin|administrator|owner|promote|transfer|permission)s?\b/i);
  await expect(alice.getByRole("combobox")).toHaveCount(1); // only the context switcher
  const aliceId = (await (await alice.request.get(`${API_URL}/api/v1/auth/session`)).json()).userId;
  for (const [method, suffix] of [
    ["PATCH", `/members/${aliceId}`],
    ["PUT", `/members/${aliceId}/role`],
    ["POST", "/transfer"],
    ["POST", "/admins"],
    ["GET", "/roles"],
  ] as const) {
    const res = await alice.request.fetch(`${API_URL}/api/v1/organizations/${state.alpha}${suffix}`, {
      method,
      headers: { "x-gozleme-client": "web", "content-type": "application/json" },
      data: method === "GET" ? undefined : { role: "admin" },
    });
    expect([404, 405], `${method} ${suffix}`).toContain(res.status());
  }
  expect((await alice.request.get(`${API_URL}/api/v1/users`)).status()).toBe(404);
});

test("observability: organization flows are traced browser → web → API → PostgreSQL with no personal data", async ({ request }) => {
  let traceId: string | undefined;
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = await recentSpans("gozleme-web-browser", 10);
        for (const candidate of spans.filter((s) => s.name === "gozleme.ui.org_create" && s.attributes["gozleme.ui.outcome"] === "success")) {
          const trace = spans.filter((s) => s.traceId === candidate.traceId);
          const api = trace.some((s) => s.service === "gozleme-api" && s.name === "gozleme.org.create" && s.attributes["gozleme.org.outcome"] === "success");
          const web = trace.some((s) => s.service === "gozleme-web");
          const db = trace.some((s) => s.service === "gozleme-api" && s.attributes["db.system.name"] === "postgresql");
          if (api && web && db) {
            traceId = candidate.traceId;
            return true;
          }
        }
        return false;
      },
      { timeout: 45_000, intervals: [1500] },
    )
    .toBe(true);

  const apiSpans: typeof spans = [];
  for (const name of ["gozleme.org.create", "gozleme.org.rename", "gozleme.org.select", "gozleme.org.invite_create", "gozleme.org.invite_revoke", "gozleme.org.invite_redeem", "gozleme.org.member_remove", "gozleme.org.member_leave"]) {
    const found = await recentSpans("gozleme-api", 15, name);
    expect(found.some((s) => s.name === name), name).toBe(true);
    apiSpans.push(...found);
  }

  const code = state.code!;
  const privateValues = [DEMO.alice, DEMO.bob, DEMO.carol, DEMO_PASSWORD, ALPHA, BETA, BETA_RENAMED, SOLO, code, code.replaceAll("-", ""), state.revokedCode!, "gozleme_session", ...secretStrings];
  const telemetry = JSON.stringify([...spans, ...apiSpans]);
  for (const v of privateValues) expect(telemetry, `traces must not contain ${v}`).not.toContain(v);

  const metrics = await (await request.get(METRICS_URL)).text();
  expect(metrics).toMatch(/gozleme_org_operations_total\{[^}]*gozleme_org_operation="invite_redeem"[^}]*\}/);
  expect(metrics).toMatch(/gozleme_org_operations_total\{[^}]*gozleme_org_failure_category="invalid_invitation"[^}]*\}/);
  expect(metrics).toMatch(/gozleme_org_operations_total\{[^}]*gozleme_org_failure_category="last_member"[^}]*\}/);
  for (const v of privateValues) expect(metrics, `metrics must not contain ${v}`).not.toContain(v);

  const page = await alice.context().newPage();
  await page.goto(`${JAEGER_URL}/trace/${traceId}`);
  await expect(page.getByText("gozleme.ui.org_create").first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("gozleme.org.create").first()).toBeVisible();
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/12-create-organization-trace-jaeger.png`, fullPage: false });
  await page.close();
});

test("privacy: organization audit events carry opaque ids only; logs hold no names, emails or codes", async () => {
  await expect
    .poll(async () => (await storedLogs('{service_name="gozleme-api"} |= "removed from"', 10)).filter((l) => l.at >= suiteStarted).length, { timeout: 45_000, intervals: [2000] })
    .toBeGreaterThan(0);
  const audit = (await storedLogs('{service_name="gozleme-api"} |~ "Organization created|Invitation (created|revoked|redeemed)|removed from|redemption refused"', 15)).filter((l) => l.at >= suiteStarted);
  expect(audit.length).toBeGreaterThan(0);
  for (const log of audit) expect(log.line).toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);

  const stored = JSON.stringify(await storedLogs('{service_name=~"gozleme-.*"}', 15));
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color"], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  const code = state.code!;
  for (const v of [DEMO.alice, DEMO.bob, DEMO.carol, ALPHA, BETA, BETA_RENAMED, SOLO, code, code.replaceAll("-", ""), state.revokedCode!, DEMO_PASSWORD]) {
    expect(stored, `stored logs must not contain ${v}`).not.toContain(v);
    expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);
  }
});
