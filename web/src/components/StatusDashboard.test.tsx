import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { BackendStatusResult } from "@/lib/status";
import { healthy } from "@/test/fixtures";
import { StatusDashboard } from "./StatusDashboard";

const ok: BackendStatusResult = { reachable: true, checkedAt: "2026-11-14T12:00:01Z", status: healthy };
const degraded: BackendStatusResult = {
  reachable: true,
  checkedAt: "2026-11-14T12:00:06Z",
  status: {
    ...healthy,
    status: "degraded",
    dependencies: [
      { name: "database", status: "unavailable", required: true, latencyMs: null, schemaVersion: null, message: "Database unreachable", capabilities: null },
    ],
  },
};
const unreachable: BackendStatusResult = { reachable: false, checkedAt: "2026-11-14T12:00:11Z", reason: "unreachable" };
const expired: BackendStatusResult = { reachable: true, checkedAt: "2026-11-14T12:00:16Z", session: "expired" };
const unverifiable: BackendStatusResult = { reachable: true, checkedAt: "2026-11-14T12:00:21Z", session: "unverifiable" };

function mockResponses(...results: BackendStatusResult[]) {
  const queue = [...results];
  return vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
    const next = queue.length > 1 ? queue.shift()! : queue[0]!;
    return new Response(JSON.stringify(next));
  });
}

async function tick(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe("StatusDashboard", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("shows healthy API and database details", async () => {
    mockResponses(ok);
    render(<StatusDashboard pollIntervalMs={5000} />);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Checking…");
    await tick(1);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Healthy");
    expect(screen.getByTestId("api-status")).toHaveTextContent("Reachable");
    expect(screen.getByTestId("api-version")).toHaveTextContent("0.1.0-test");
    expect(screen.getByTestId("db-status")).toHaveTextContent("Healthy");
    expect(screen.getByTestId("db-schema-version")).toHaveTextContent("20261114000000_Foundation");
    expect(screen.getByTestId("db-vector")).toHaveTextContent("Available, not enabled");
    expect(screen.getByTestId("last-checked")).toHaveTextContent("12:00:01 UTC");
    expect(screen.getByRole("status")).toHaveAttribute("aria-live", "polite");
  });

  it("switches to degraded when the database becomes unavailable, then recovers", async () => {
    mockResponses(ok, degraded, ok);
    render(<StatusDashboard pollIntervalMs={5000} />);
    await tick(1);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Healthy");
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Degraded");
    expect(screen.getByTestId("db-status")).toHaveTextContent("Unavailable");
    expect(screen.getByTestId("db-detail")).toHaveTextContent("Database unreachable");
    expect(screen.queryByTestId("db-schema-version")).not.toBeInTheDocument();
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Healthy");
  });

  it("shows API unreachable and suppresses stale database data", async () => {
    mockResponses(ok, unreachable);
    render(<StatusDashboard pollIntervalMs={5000} />);
    await tick(1);
    expect(screen.getByTestId("db-schema-version")).toBeInTheDocument();
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("API unreachable");
    expect(screen.getByTestId("api-status")).toHaveTextContent("Unreachable");
    expect(screen.getByTestId("db-status")).toHaveTextContent("Unknown");
    expect(screen.queryByTestId("db-schema-version")).not.toBeInTheDocument();
    expect(screen.queryByTestId("api-version")).not.toBeInTheDocument();
  });

  it("treats a failing same-origin route as unreachable", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("network"));
    render(<StatusDashboard pollIntervalMs={5000} />);
    await tick(1);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("API unreachable");
  });

  it("shows an expired session without any status data and offers sign-in", async () => {
    mockResponses(ok, expired);
    render(<StatusDashboard pollIntervalMs={5000} />);
    await tick(1);
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Signed out");
    expect(screen.getByTestId("session-notice")).toHaveTextContent("Your session has expired.");
    expect(screen.getByRole("link", { name: "Sign in again" })).toHaveAttribute("href", "/login?returnTo=%2F");
    expect(screen.queryByTestId("api-version")).not.toBeInTheDocument();
    expect(screen.queryByTestId("db-status")).not.toBeInTheDocument();
  });

  it("shows an honest degraded view when the session can't be verified (FR-021a)", async () => {
    mockResponses(ok, unverifiable, ok);
    render(<StatusDashboard pollIntervalMs={5000} />);
    await tick(1);
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Degraded");
    expect(screen.getByTestId("api-status")).toHaveTextContent("Reachable");
    expect(screen.getByTestId("db-status")).toHaveTextContent("Unavailable");
    expect(screen.getByTestId("db-detail")).toHaveTextContent("Database unreachable");
    expect(screen.getByTestId("session-notice")).toHaveTextContent("can't be verified");
    expect(screen.queryByTestId("api-version")).not.toBeInTheDocument();
    expect(screen.queryByTestId("db-schema-version")).not.toBeInTheDocument();
    await tick(5000);
    expect(screen.getByTestId("overall-status")).toHaveTextContent("Healthy");
    expect(screen.queryByTestId("session-notice")).not.toBeInTheDocument();
  });

  it("renders the account controls it is given", async () => {
    mockResponses(ok);
    render(<StatusDashboard pollIntervalMs={5000} account={<p data-testid="acct">alice</p>} />);
    expect(screen.getByTestId("acct")).toHaveTextContent("alice");
  });
});
