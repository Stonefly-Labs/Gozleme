import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { compose, forbiddenStrings, secretStrings } from "./compose";
import { shotsDir } from "./evidence";
import {
  GRAFANA_URL,
  JAEGER_URL,
  METRICS_URL,
  promQuery,
  recentSpans,
  servicesByTrace,
  storedLogs,
  traceById,
} from "./telemetry";

/**
 * GOZ-0000 autonomous demo: live dashboard reading real API + PostgreSQL status, real
 * dependency failures (containers stopped), recovery, and the catalog boundary (organizations
 * superseded by GOZ-0002).
 *
 * Narrowed by GOZ-0001 (IF-01, FR-036/FR-037): runs signed in as the seeded synthetic account (the
 * project's storageState; the cookie is host-scoped, so direct API calls carry it too). While the
 * database is down the session cannot be verified, so status data is withheld (fail closed,
 * FR-021a) and the dashboard shows the honest degraded view.
 */
const UNREACHABLE_503 = /^Database unreachable; request answered 503 \((NpgsqlException|PostgresException)\)$/;
const shots = shotsDir("goz-0000");
const LATEST_MIGRATION = /_TenantRls$/;

test.describe.configure({ mode: "serial" });

async function expectNoSecrets(page: Page, ...bodies: string[]) {
  await expectNotContained(page, forbiddenStrings, ...bodies);
}

async function expectNotContained(page: Page, strings: string[], ...bodies: string[]) {
  const text = [await page.locator("body").innerText(), ...bodies].join("\n");
  for (const s of strings) expect(text, `must not contain ${s}`).not.toContain(s);
}

/** FR-006: WCAG 2.1 A/AA checks (contrast, landmarks, names) on the current dashboard state. */
async function expectAccessible(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
}

test.afterAll(() => {
  // Leave the stack as we found it even if a step failed midway.
  compose("start", "postgres", "api", "otel-collector");
});

test("healthy: dashboard shows real API and database status matching the API", async ({ page, request }) => {
  const apiRes = await request.get(`${API_URL}/api/v1/status`);
  expect(apiRes.ok()).toBeTruthy();
  const api = await apiRes.json();
  expect(api.status).toBe("healthy");
  const db = api.dependencies.find((d: { name: string }) => d.name === "database");

  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  await expect(page.getByTestId("api-status")).toHaveText("Reachable");
  await expect(page.getByTestId("api-version")).toHaveText(api.version);
  await expect(page.getByTestId("api-service")).toHaveText(api.service);
  await expect(page.getByTestId("db-status")).toHaveText("Healthy");
  await expect(page.getByTestId("db-schema-version")).toHaveText(db.schemaVersion);
  expect(db.schemaVersion).toMatch(LATEST_MIGRATION);
  await expect(page.getByTestId("db-vector")).toHaveText(
    db.capabilities.vector.available ? "Available, not enabled" : "Not installed",
  );
  await expect(page.getByTestId("last-checked")).toContainText("Last checked");
  await expectNoSecrets(page, await apiRes.text());
  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/01-healthy.png`, fullPage: true });
});

test("degraded: stopping PostgreSQL shows Degraded within 15 s, readiness 503, liveness 200", async ({ page, request }) => {
  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");

  compose("stop", "postgres");
  await expect(page.getByTestId("overall-status")).toHaveText("Degraded", { timeout: 15_000 });
  await expect(page.getByTestId("db-status")).toHaveText("Unavailable");
  await expect(page.getByTestId("db-detail")).toHaveText("Database unreachable");
  await expect(page.getByTestId("db-schema-version")).toHaveCount(0);
  await expect(page.getByTestId("api-status")).toHaveText("Reachable");

  const ready = await request.get(`${API_URL}/health/ready`);
  const live = await request.get(`${API_URL}/health/live`);
  expect(ready.status()).toBe(503);
  expect(live.status()).toBe(200);
  const status = await request.get(`${API_URL}/api/v1/status`);
  expect(status.status()).toBe(503); // GOZ-0001: session unverifiable → no status data
  expect(await status.json()).toEqual({ error: "unavailable" });
  await expect(page.getByTestId("session-notice")).toContainText("can't be verified");
  await expect(page.getByTestId("api-version")).toHaveCount(0);
  await expectNoSecrets(page, await status.text(), await ready.text());
  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/02-database-down-degraded.png`, fullPage: true });
});

test("recovery: restarting PostgreSQL returns to Healthy within 30 s without restarting api/web", async ({ page }) => {
  await page.goto("/");
  compose("start", "postgres");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy", { timeout: 30_000 });
  await expect(page.getByTestId("db-schema-version")).toHaveText(LATEST_MIGRATION);
  await expect(page.getByTestId("session-notice")).toHaveCount(0); // same session honoured again
  await page.screenshot({ path: `${shots}/03-recovered-healthy.png`, fullPage: true });
});

test("unreachable: stopping the API shows API unreachable and no stale database data", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");

  compose("stop", "api");
  await expect(page.getByTestId("overall-status")).toHaveText("API unreachable", { timeout: 15_000 });
  await expect(page.getByTestId("api-status")).toHaveText("Unreachable");
  await expect(page.getByTestId("db-status")).toHaveText("Unknown");
  await expect(page.getByTestId("db-schema-version")).toHaveCount(0);
  await expect(page.getByTestId("api-version")).toHaveCount(0);
  await expectNoSecrets(page);
  await expectAccessible(page);
  await page.screenshot({ path: `${shots}/04-api-unreachable.png`, fullPage: true });

  compose("start", "api");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy", { timeout: 60_000 });
});

test("boundary: catalog features are not implemented", async ({ page, request }) => {
  // Narrowed by GOZ-0002 (IF-10): organizations now exist and are covered by the goz-0002 suite.
  for (const [method, path] of [
    ["GET", "/api/v1/catalog"],
    ["GET", "/api/v1/users"],
    ["GET", "/api/v1/search?q=mcp"],
    ["POST", "/api/v1/auth/login"],
  ] as const) {
    const res = await request.fetch(`${API_URL}${path}`, { method });
    expect(res.status(), `${method} ${path}`).toBe(404);
  }
  // `/login` is superseded by GOZ-0001 local sign-in and `/organizations` by GOZ-0002; the catalog still does not exist.
  const res = await page.goto("/catalog");
  expect(res?.status(), "/catalog").toBe(404);
  await page.goto("/");
  // The only navigation is the GOZ-0002 organizations header: no catalog links yet.
  await expect(page.getByRole("banner").getByRole("link")).toHaveText(["Organizations", "Join"]);
  await expect(page.getByRole("link")).toHaveCount(2);
});

test("observability: one trace threads browser → web → API → PostgreSQL, with metrics and no secrets", async ({ page, request }) => {
  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  await page.getByRole("button", { name: "Check now" }).click();

  const want = ["gozleme-web-browser", "gozleme-web", "gozleme-api"];
  let traceId: string | undefined;
  let spans: Awaited<ReturnType<typeof recentSpans>> = [];
  await expect
    .poll(
      async () => {
        spans = await recentSpans("gozleme-web-browser");
        for (const [id, services] of servicesByTrace(spans)) {
          const hasDb = spans.some((s) => s.traceId === id && s.service === "gozleme-api" && s.attributes["db.system.name"] === "postgresql");
          if (want.every((w) => services.has(w)) && hasDb) {
            traceId = id;
            return true;
          }
        }
        return false;
      },
      { timeout: 30_000, intervals: [1000] },
    )
    .toBe(true);

  const trace = spans.filter((s) => s.traceId === traceId);
  expect(trace.some((s) => s.service === "gozleme-web-browser" && /fetch|GET/i.test(s.name))).toBe(true);
  expect(trace.some((s) => s.service === "gozleme-api" && s.name === "status.probe database")).toBe(true);
  const telemetryText = JSON.stringify(trace);
  for (const s of secretStrings) expect(telemetryText, `trace must not contain ${s}`).not.toContain(s);

  const metrics = await (await request.get(METRICS_URL)).text();
  expect(metrics).toMatch(/gozleme_status_checks_total\{[^}]*gozleme_status_outcome="healthy"[^}]*service_name="gozleme-api"/);
  expect(metrics).toContain("http_server_request_duration_seconds");
  for (const s of [...secretStrings, "Host=", "Username="]) {
    expect(metrics, `metrics must not contain ${s}`).not.toContain(s);
  }

  await page.goto(`${JAEGER_URL}/trace/${traceId}`);
  await expect(page.getByText("gozleme-web-browser").first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("status.probe database").first()).toBeVisible();
  await expectNotContained(page, secretStrings);
  await page.screenshot({ path: `${shots}/05-distributed-trace-jaeger.png`, fullPage: false });
});

test("stored logs: outage warnings are stored in Loki, correlated to their trace, metrics stored, no secrets", async ({ page, request }) => {
  const started = Date.now(); // ignore lines from earlier runs still inside Loki's query window
  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  compose("stop", "postgres");
  await expect(page.getByTestId("overall-status")).toHaveText("Degraded", { timeout: 15_000 });
  // GOZ-0001 fails closed on status while the database is down; the anonymous readiness probe still probes.
  expect((await request.get(`${API_URL}/health/ready`)).status()).toBe(503);
  compose("start", "postgres");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy", { timeout: 30_000 });

  // The probe's warning is stored (readiness requests are not traced, so it carries no stored trace).
  await expect
    .poll(async () => (await storedLogs('{service_name="gozleme-api"} |= "Database probe failed"', 5)).map((l) => l.line), { timeout: 45_000, intervals: [2000] })
    .toContain("Database probe failed (NpgsqlException)");

  // The dashboard's status requests during the outage were answered 503 with a fixed warning,
  // correlated to the stored browser → web → API trace of that request.
  let correlated: { traceId: string; line: string } | undefined;
  await expect
    .poll(
      async () => {
        const logs = await storedLogs('{service_name="gozleme-api"} |= "Database unreachable; request answered 503"', 5);
        for (const log of logs.filter((l) => l.labels.trace_id && l.at >= started)) {
          const spans = await traceById(log.labels.trace_id!);
          if (spans.some((s) => s.service === "gozleme-api" && /\/api\/v1\/status/.test(s.name + JSON.stringify(s.attributes)))) {
            correlated = { traceId: log.labels.trace_id!, line: log.line };
            return true;
          }
        }
        return false;
      },
      { timeout: 45_000, intervals: [2000] },
    )
    .toBe(true);
  // The logged type is the connection-level cause: a refused connection or PostgreSQL's admin shutdown.
  expect(correlated!.line).toMatch(UNREACHABLE_503);

  const allLogs = await storedLogs('{service_name=~"gozleme-.*"}', 15);
  expect(allLogs.length).toBeGreaterThan(0);
  const logText = JSON.stringify(allLogs);
  for (const s of [...secretStrings, "Host=", "Username="]) {
    expect(logText, `stored logs must not contain ${s}`).not.toContain(s);
  }

  await expect.poll(() => promQuery('gozleme_status_checks_total{gozleme_status_outcome="unavailable"}'), { timeout: 30_000 }).toBeGreaterThan(0);

  const panes = {
    a: {
      datasource: "loki",
      queries: [{ refId: "A", expr: '{service_name="gozleme-api"} |~ "Database probe failed|Database unreachable"', datasource: { type: "loki", uid: "loki" } }],
      range: { from: "now-15m", to: "now" },
    },
  };
  await page.goto(`${GRAFANA_URL}/explore?schemaVersion=1&orgId=1&panes=${encodeURIComponent(JSON.stringify(panes))}`);
  // Grafana renders the line inside a larger row, so match it unanchored there.
  await expect(page.getByText(new RegExp(UNREACHABLE_503.source.slice(1, -1))).first()).toBeVisible({ timeout: 30_000 });
  await expectNotContained(page, [...secretStrings, "Host=", "Username="]);
  await page.screenshot({ path: `${shots}/06-stored-logs-grafana.png`, fullPage: false });
});

test("observability resilience: the app stays healthy when the telemetry collector is down", async ({ page, request }) => {
  compose("stop", "otel-collector");
  await page.goto("/");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  await page.getByRole("button", { name: "Check now" }).click();
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy");
  expect((await request.get("http://localhost:8080/health/ready")).status()).toBe(200);
  compose("start", "otel-collector");
});
