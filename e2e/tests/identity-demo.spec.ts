import AxeBuilder from "@axe-core/playwright";
import { execFileSync } from "node:child_process";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { API_URL, WEB_URL } from "../playwright.config";
import { compose, composeWithEnv, repoRoot, secretStrings } from "./compose";
import { DEMO, DEMO_PASSWORD, shotsDir } from "./evidence";
import { JAEGER_URL, METRICS_URL, recentSpans, storedLogs } from "./telemetry";

/**
 * GOZ-0001 autonomous demo: register, sign in and sign out; the protected dashboard rejects
 * anonymous access; sign-out-everywhere; lockout with generic failures; operator-closed
 * registration; no external OIDC; one traced sign-in with no personal data in telemetry.
 * Accounts are synthetic (`*.test`); the seeded ones come from `docker compose --profile demo run --rm seed`.
 */
const shots = shotsDir("goz-0001");
const GENERIC_FAILURE = "Email or password is incorrect.";
const suiteStarted = Date.now(); // Loki windows can hold earlier runs' lines
const registered = { email: `demo-${Date.now()}@example.test`, password: "a-synthetic-demo-passphrase" };

test.describe.configure({ mode: "serial" });

async function expectAccessible(page: Page) {
  // Client-side navigations stream the new page's metadata; audit once its title has arrived.
  await expect(page).toHaveTitle(/ — Gözleme$|^Gözleme — /);
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
}

async function signIn(page: Page, email: string, password: string) {
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

async function signedInPage(browser: Browser, email: string, password = DEMO_PASSWORD) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto("/login");
  await signIn(page, email, password);
  await expect(page.getByTestId("account-email")).toHaveText(email);
  return page;
}

/** POST from inside the page, so the browser itself sends `Sec-Fetch-Site: same-origin` like the real UI. */
async function browserPost(page: Page, path: string, body: unknown) {
  return page.evaluate(
    async ([p, b]) => {
      const res = await fetch(p, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(b) });
      return { status: res.status, body: await res.text() };
    },
    [path, body] as const,
  );
}

/** Calls the API directly the way the web tier does (internal header), for byte-level comparisons. */
async function apiSignIn(email: string, password: string) {
  const res = await fetch(`${API_URL}/api/v1/auth/sign-in`, {
    method: "POST",
    headers: { "content-type": "application/json", "x-gozleme-client": "web" },
    body: JSON.stringify({ email, password }),
  });
  return { status: res.status, body: await res.text(), cookie: res.headers.get("set-cookie") };
}

test.afterAll(() => {
  // Leave the API as we found it even if the closed-registration step failed midway.
  composeWithEnv({ GOZLEME_REGISTRATION_ENABLED: "true" }, "up", "-d", "--wait", "--no-deps", "api");
});

test("anonymous: the dashboard redirects to sign-in and status data is refused", async ({ page, request }) => {
  const res = await page.goto("/");
  await expect(page).toHaveURL(`${WEB_URL}/login?returnTo=%2F`);
  expect(res?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.getByTestId("overall-status")).toHaveCount(0);

  const api = await request.get(`${API_URL}/api/v1/status`);
  expect(api.status()).toBe(401);
  expect(await api.text()).toBe("");
  const proxied = await request.get("/api/backend-status");
  expect(proxied.status()).toBe(401);
  expect(await proxied.json()).toEqual({ reachable: true, checkedAt: expect.any(String), session: "expired" });

  // Probes stay anonymous (FR-022).
  expect((await request.get(`${API_URL}/health/live`)).status()).toBe(200);
  expect((await request.get(`${API_URL}/health/ready`)).status()).toBe(200);
  expect((await request.get("/api/live")).status()).toBe(200);

  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/01-anonymous-redirected-to-sign-in.png`, fullPage: true });
});

test("register: field errors, then a new synthetic account lands on the protected dashboard", async ({ page }) => {
  await page.goto("/register");
  await page.getByLabel("Email").fill(registered.email);
  await page.getByLabel("Password", { exact: true }).fill("too-short");
  await page.getByLabel("Confirm password").fill("too-short");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByTestId("password-error")).toHaveText("Use at least 12 characters.");

  await page.getByLabel("Password", { exact: true }).fill("password1234");
  await page.getByLabel("Confirm password").fill("password1234");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByTestId("password-error")).toContainText("too common");
  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/02-register-validation.png`, fullPage: true });

  const started = Date.now();
  await page.getByLabel("Password", { exact: true }).fill(registered.password);
  await page.getByLabel("Confirm password").fill(registered.password);
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page).toHaveURL(`${WEB_URL}/`);
  await expect(page.getByTestId("account-email")).toHaveText(registered.email);
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  test.info().annotations.push({ type: "timing", description: `register→dashboard ${Date.now() - started} ms` });
  expect(Date.now() - started).toBeLessThan(60_000); // SC-001

  const cookie = (await page.context().cookies()).find((c) => c.name === "gozleme_session")!;
  expect(cookie.httpOnly).toBe(true);
  expect(cookie.sameSite).toBe("Lax");
  expect(await page.evaluate(() => document.cookie)).not.toContain("gozleme_session"); // not script-readable

  // Duplicate registration is generic and does not confirm the account exists (FR-007).
  const dup = await browserPost(page, "/api/auth/register", { email: registered.email, password: "another-long-passphrase" });
  expect(dup).toEqual({ status: 409, body: '{"error":"registration_failed"}' });
  // The same request without browser fetch metadata or Origin is refused as cross-site (FR-014).
  const forged = await page.request.post("/api/auth/register", { data: { email: registered.email, password: "another-long-passphrase" } });
  expect(forged.status()).toBe(403);

  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/03-registered-dashboard.png`, fullPage: true });
});

test("sign out ends the session server-side; a replayed cookie is anonymous; sign-in returns to the page", async ({ browser }) => {
  const page = await signedInPage(browser, registered.email, registered.password);
  const captured = (await page.context().cookies()).find((c) => c.name === "gozleme_session")!;

  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(page).toHaveURL(`${WEB_URL}/login`);
  await expect(page).toHaveTitle("Sign in — Gözleme");
  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/04-signed-out.png`, fullPage: true });

  // Replaying the captured cookie from a fresh browser is treated as anonymous (US2-AS2, SC-003).
  const replay = await browser.newContext();
  await replay.addCookies([captured]);
  const replayPage = await replay.newPage();
  await replayPage.goto("/");
  await expect(replayPage).toHaveURL(`${WEB_URL}/login?returnTo=%2F`);
  const direct = await fetch(`${API_URL}/api/v1/auth/session`, { headers: { cookie: `gozleme_session=${captured.value}` } });
  expect(direct.status).toBe(401);

  // A returning user signs in and is sent back to the page they asked for; off-site targets are ignored.
  const started = Date.now();
  await replayPage.goto("/login?returnTo=%2F%3Fsource%3Ddemo");
  await signIn(replayPage, registered.email, registered.password);
  await expect(replayPage).toHaveURL(`${WEB_URL}/?source=demo`);
  await expect(replayPage.getByTestId("account-email")).toHaveText(registered.email);
  test.info().annotations.push({ type: "timing", description: `sign-in→dashboard ${Date.now() - started} ms` });
  expect(Date.now() - started).toBeLessThan(15_000); // SC-001

  const off = await signedInPage(browser, DEMO.bob);
  await off.goto("/login?returnTo=https%3A%2F%2Fevil.example%2F");
  await expect(off).toHaveURL(`${WEB_URL}/`); // already signed in → safe default, never off-site
});

test("sign out everywhere ends the account's sessions in every browser", async ({ browser }) => {
  const first = await signedInPage(browser, DEMO.bob);
  const second = await signedInPage(browser, DEMO.bob);
  const other = await signedInPage(browser, DEMO.alice);

  await first.getByRole("button", { name: "Sign out everywhere" }).click();
  await expect(first).toHaveURL(`${WEB_URL}/login`);

  await second.reload();
  await expect(second).toHaveURL(`${WEB_URL}/login?returnTo=%2F`);
  await other.reload();
  await expect(other.getByTestId("account-email")).toHaveText(DEMO.alice); // other accounts unaffected
  await second.screenshot({ path: `${shots}/05-signed-out-everywhere-second-browser.png`, fullPage: true });
});

test("lockout: five failures lock the account; every failure reads exactly like an unknown email", async ({ page }) => {
  await page.goto("/login");
  for (let i = 0; i < 5; i++) {
    await signIn(page, DEMO.lockout, `wrong-password-${i}`);
    await expect(page.getByTestId("form-error")).toHaveText(GENERIC_FAILURE);
  }
  // Locked: the correct password is refused with the same message (FR-018, SC-005).
  await signIn(page, DEMO.lockout, DEMO_PASSWORD);
  await expect(page.getByTestId("form-error")).toHaveText(GENERIC_FAILURE);
  await expect(page).toHaveURL(`${WEB_URL}/login`);

  // Wrong password, unknown email and locked account are byte-identical at the API (FR-020, SC-004).
  const locked = await apiSignIn(DEMO.lockout, DEMO_PASSWORD);
  const unknown = await apiSignIn(`nobody-${Date.now()}@example.test`, DEMO_PASSWORD);
  const wrong = await apiSignIn(DEMO.alice, "definitely-not-the-password");
  for (const r of [locked, unknown, wrong]) {
    expect(r.status).toBe(401);
    expect(r.body).toBe('{"error":"invalid_credentials"}');
    expect(r.cookie).toBeNull();
  }

  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/06-lockout-generic-error.png`, fullPage: true });
});

test("operator closes registration: new accounts are refused, existing accounts still sign in", async ({ browser }) => {
  composeWithEnv({ GOZLEME_REGISTRATION_ENABLED: "false" }, "up", "-d", "--wait", "--no-deps", "api");
  try {
    const page = await (await browser.newContext()).newPage(); // axe needs an explicit context
    await page.goto("/register");
    await expect(page.getByTestId("registration-closed")).toContainText("Registration is closed");
    await expect(page.getByLabel("Email")).toHaveCount(0);
    const refused = await browserPost(page, "/api/auth/register", { email: `late-${Date.now()}@example.test`, password: "a-long-passphrase-here" });
    expect(refused).toEqual({ status: 403, body: '{"error":"registration_closed"}' });

    await page.goto("/login");
    await expect(page.getByRole("link", { name: "Create an account" })).toHaveCount(0);
    await expectAccessible(page);

    await page.goto("/register");
    await page.screenshot({ path: `${shots}/07-registration-closed.png`, fullPage: true });

    const existing = await signedInPage(browser, DEMO.alice); // sessions and accounts are unaffected
    await expect(existing.getByTestId("overall-status")).toHaveText("Healthy");
  } finally {
    composeWithEnv({ GOZLEME_REGISTRATION_ENABLED: "true" }, "up", "-d", "--wait", "--no-deps", "api");
  }
});

test("boundary: no external identity provider is offered, configured or reachable", async ({ page, request }) => {
  await page.goto("/login");
  await expect(page.getByText(/single sign-on|\bsso\b|openid|oidc|google|github|microsoft|saml/i)).toHaveCount(0);
  await expect(page.getByRole("button")).toHaveText(["Sign in"]);

  const options = await request.get("/api/auth/options");
  expect(await options.json()).toEqual({ registrationEnabled: true, externalProviders: [] });

  for (const path of ["/signin-oidc", "/signin-google", "/.well-known/openid-configuration", "/api/v1/auth/external/github"]) {
    expect((await request.get(`${API_URL}${path}`)).status(), `api ${path}`).toBe(404);
  }
  for (const path of ["/signin-oidc", "/auth/callback", "/api/auth/callback/oidc"]) {
    expect((await request.get(path)).status(), `web ${path}`).toBe(404);
  }
  // The operator recovery surface is not HTTP (FR-024).
  for (const path of ["/api/v1/users", "/api/v1/auth/reset-password", "/api/v1/auth/unlock"]) {
    expect((await request.fetch(`${API_URL}${path}`, { method: path.endsWith("users") ? "GET" : "POST" })).status(), path).toBe(404);
  }
});

test("observability: a sign-in is one browser → web → API → PostgreSQL trace with no personal data", async ({ browser, request }) => {
  const page = await signedInPage(browser, DEMO.bob);
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");

  let traceId: string | undefined;
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = await recentSpans("gozleme-web-browser", 5);
        const ui = spans.filter((s) => s.name === "gozleme.ui.sign_in" && s.attributes["gozleme.ui.outcome"] === "success");
        for (const candidate of ui) {
          const trace = spans.filter((s) => s.traceId === candidate.traceId);
          const api = trace.some((s) => s.service === "gozleme-api" && s.name === "gozleme.auth.sign_in" && s.attributes["gozleme.auth.outcome"] === "success");
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

  const privateValues = [DEMO.alice, DEMO.bob, DEMO.lockout, registered.email, registered.password, DEMO_PASSWORD, "gozleme_session", ...secretStrings];
  const allTelemetry = JSON.stringify(spans);
  for (const v of privateValues) expect(allTelemetry, `traces must not contain ${v}`).not.toContain(v);

  const metrics = await (await request.get(METRICS_URL)).text();
  expect(metrics).toMatch(/gozleme_auth_attempts_total\{[^}]*gozleme_auth_failure_category="locked_out"[^}]*\}/);
  expect(metrics).toMatch(/gozleme_auth_attempts_total\{[^}]*gozleme_auth_operation="sign_out_all"[^}]*\}/);
  for (const v of privateValues) expect(metrics, `metrics must not contain ${v}`).not.toContain(v);

  await page.goto(`${JAEGER_URL}/trace/${traceId}`);
  await expect(page.getByText("gozleme.ui.sign_in").first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("gozleme.auth.sign_in").first()).toBeVisible();
  for (const v of privateValues) await expect(page.locator("body")).not.toContainText(v);
  await page.screenshot({ path: `${shots}/08-sign-in-trace-jaeger.png`, fullPage: false });
});

test("privacy: audit events are stored with opaque ids; logs hold no emails, passwords or cookies", async () => {
  await expect
    .poll(async () => (await storedLogs('{service_name="gozleme-api"} |= "Account locked out; sessions revoked"', 10)).filter((l) => l.at >= suiteStarted).length, { timeout: 45_000, intervals: [2000] })
    .toBeGreaterThan(0);
  const audit = await storedLogs('{service_name="gozleme-api"} |~ "Signed in|Sign-in failed|Signed out|Account registered|locked out"', 15);
  expect(audit.length).toBeGreaterThan(0);
  // Each audit line ends in the account's opaque id, or "(null)" when no account matched (unknown email).
  for (const log of audit) expect(log.line).toMatch(/ (?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\(null\))$/);

  const stored = JSON.stringify(await storedLogs('{service_name=~"gozleme-.*"}', 15));
  const composeLogs = execFileSync("docker", ["compose", "logs", "--no-color"], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 }).toString();
  for (const v of [DEMO.alice, DEMO.bob, DEMO.lockout, registered.email, registered.password, DEMO_PASSWORD, "gozleme_session="]) {
    expect(stored, `stored logs must not contain ${v}`).not.toContain(v);
    expect(composeLogs, `compose logs must not contain ${v}`).not.toContain(v);
  }
  compose("ps"); // stack still up for the run record
});
