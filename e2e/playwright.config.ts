import { defineConfig, devices } from "@playwright/test";
import { AUTH_STATE } from "./tests/evidence";

export const WEB_URL = process.env.GOZLEME_WEB_URL ?? "http://localhost:3000";
export const API_URL = process.env.GOZLEME_API_URL ?? "http://localhost:8080";

const chrome = { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } };

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  forbidOnly: !!process.env.CI,
  reporter: [
    ["list"],
    ["json", { outputFile: process.env.GOZLEME_DEMO_RESULTS ?? "../.demo-tmp/playwright-results.json" }],
    ["html", { open: "never", outputFolder: "playwright-report" }],
  ],
  use: {
    baseURL: WEB_URL,
    viewport: { width: 1280, height: 800 },
    colorScheme: "light",
    trace: "retain-on-failure",
  },
  // One project per spec demo, chained so `--project goz-NNNN` replays every earlier demo first (IF-08).
  projects: [
    { name: "setup", testMatch: /auth\.setup\.ts/, use: chrome },
    { name: "goz-0000", testMatch: /bootstrap-demo\.spec\.ts/, dependencies: ["setup"], use: { ...chrome, storageState: AUTH_STATE } },
    { name: "goz-0001", testMatch: /identity-demo\.spec\.ts/, dependencies: ["goz-0000"], use: chrome },
    { name: "goz-0002", testMatch: /organizations-demo\.spec\.ts/, dependencies: ["goz-0001"], use: chrome },
  ],
});
