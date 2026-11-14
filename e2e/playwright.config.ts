import { defineConfig, devices } from "@playwright/test";

export const WEB_URL = process.env.GOZLEME_WEB_URL ?? "http://localhost:3000";
export const API_URL = process.env.GOZLEME_API_URL ?? "http://localhost:8080";

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
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } } }],
});
