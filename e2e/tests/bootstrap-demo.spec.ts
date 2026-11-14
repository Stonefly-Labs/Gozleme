import { expect, test, type Page } from "@playwright/test";
import { API_URL } from "../playwright.config";
import { compose, forbiddenStrings, repoRoot } from "./compose";

/**
 * GOZ-0000 autonomous demo: live dashboard reading real API + PostgreSQL status, real
 * dependency failures (containers stopped), recovery, and the identity/catalog boundary.
 */
const shots = `${repoRoot}docs/specs/000-repository-bootstrap/evidence/screenshots`;

test.describe.configure({ mode: "serial" });

async function expectNoSecrets(page: Page, ...bodies: string[]) {
  const text = [await page.locator("body").innerText(), ...bodies].join("\n");
  for (const s of forbiddenStrings) expect(text, `must not contain ${s}`).not.toContain(s);
}

test.afterAll(() => {
  // Leave the stack as we found it even if a step failed midway.
  compose("start", "postgres", "api");
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
  expect(db.schemaVersion).toMatch(/_Foundation$/);
  await expect(page.getByTestId("db-vector")).toHaveText(
    db.capabilities.vector.available ? "Available, not enabled" : "Not installed",
  );
  await expect(page.getByTestId("last-checked")).toContainText("Last checked");
  await expectNoSecrets(page, await apiRes.text());
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
  expect((await status.json()).status).toBe("degraded");
  await expectNoSecrets(page, await status.text(), await ready.text());
  await page.screenshot({ path: `${shots}/02-database-down-degraded.png`, fullPage: true });
});

test("recovery: restarting PostgreSQL returns to Healthy within 30 s without restarting api/web", async ({ page }) => {
  await page.goto("/");
  compose("start", "postgres");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy", { timeout: 30_000 });
  await expect(page.getByTestId("db-schema-version")).toHaveText(/_Foundation$/);
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
  await page.screenshot({ path: `${shots}/04-api-unreachable.png`, fullPage: true });

  compose("start", "api");
  await expect(page.getByTestId("overall-status")).toHaveText("Healthy", { timeout: 60_000 });
});

test("boundary: identity, organization and catalog features are not implemented", async ({ page, request }) => {
  for (const [method, path] of [
    ["GET", "/api/v1/catalog"],
    ["GET", "/api/v1/organizations"],
    ["GET", "/api/v1/users"],
    ["GET", "/api/v1/search?q=mcp"],
    ["POST", "/api/v1/auth/login"],
  ] as const) {
    const res = await request.fetch(`${API_URL}${path}`, { method });
    expect(res.status(), `${method} ${path}`).toBe(404);
  }
  for (const path of ["/login", "/catalog", "/organizations"]) {
    const res = await page.goto(path);
    expect(res?.status(), path).toBe(404);
  }
  await page.goto("/");
  await expect(page.getByRole("link")).toHaveCount(0);
  await expect(page.getByText(/sign in|log in/i)).toHaveCount(0);
});
