"use client";

import { useCallback, useEffect, useState } from "react";
import type { BackendStatusResult, DependencyStatus, UnreachableReason } from "@/lib/status";

export const POLL_INTERVAL_MS = 5000;

type Tone = "ok" | "warn" | "down" | "unknown";

const reasonText: Record<UnreachableReason, string> = {
  timeout: "The API did not respond in time.",
  unreachable: "The API could not be reached.",
  "invalid-response": "The API returned an unexpected response.",
};

function overall(result: BackendStatusResult | null): { label: string; tone: Tone } {
  if (!result) return { label: "Checking…", tone: "unknown" };
  if (!result.reachable) return { label: "API unreachable", tone: "down" };
  switch (result.status.status) {
    case "healthy":
      return { label: "Healthy", tone: "ok" };
    case "degraded":
      return { label: "Degraded", tone: "warn" };
    default:
      return { label: "Unavailable", tone: "down" };
  }
}

function formatTime(iso: string) {
  return new Date(iso).toLocaleTimeString("en-GB", { hour12: false, timeZone: "UTC" }) + " UTC";
}

function Badge({ tone, children, testId }: { tone: Tone; children: React.ReactNode; testId?: string }) {
  return (
    <span className={`badge badge-${tone}`} data-testid={testId}>
      <span aria-hidden="true" className="badge-dot" />
      {children}
    </span>
  );
}

function Row({ label, value, testId }: { label: string; value: React.ReactNode; testId?: string }) {
  return (
    <div className="row">
      <dt>{label}</dt>
      <dd data-testid={testId}>{value}</dd>
    </div>
  );
}

function DatabaseCard({ db, apiReachable }: { db: DependencyStatus | undefined; apiReachable: boolean }) {
  // When the API is unreachable we know nothing current about the database: never show stale values.
  const known = apiReachable && db;
  const tone: Tone = !known ? "unknown" : db.status === "healthy" ? "ok" : "down";
  const label = !known ? "Unknown" : db.status === "healthy" ? "Healthy" : "Unavailable";
  const vector = known && db.capabilities?.vector;
  return (
    <section className="card" aria-labelledby="db-heading">
      <h2 id="db-heading">Database</h2>
      <dl>
        <Row label="Status" value={<Badge tone={tone} testId="db-status">{label}</Badge>} />
        {!known && <Row label="Detail" value="No current data while the API is unreachable." testId="db-detail" />}
        {known && db.message && <Row label="Detail" value={db.message} testId="db-detail" />}
        {known && db.status === "healthy" && (
          <>
            <Row label="Schema version" value={<code>{db.schemaVersion}</code>} testId="db-schema-version" />
            <Row label="Latency" value={`${db.latencyMs} ms`} testId="db-latency" />
          </>
        )}
        {vector && (
          <Row
            label="Vector search extension (optional)"
            value={vector.enabled ? "Enabled" : vector.available ? "Available, not enabled" : "Not installed"}
            testId="db-vector"
          />
        )}
      </dl>
    </section>
  );
}

export function StatusDashboard({ pollIntervalMs = POLL_INTERVAL_MS }: { pollIntervalMs?: number }) {
  const [result, setResult] = useState<BackendStatusResult | null>(null);

  const refresh = useCallback(async () => {
    try {
      const res = await fetch("/api/backend-status", { cache: "no-store" });
      setResult((await res.json()) as BackendStatusResult);
    } catch {
      setResult({ reachable: false, checkedAt: new Date().toISOString(), reason: "unreachable" });
    }
  }, []);

  useEffect(() => {
    const first = setTimeout(refresh, 0);
    const id = setInterval(refresh, pollIntervalMs);
    return () => {
      clearTimeout(first);
      clearInterval(id);
    };
  }, [refresh, pollIntervalMs]);

  const o = overall(result);
  const db = result?.reachable ? result.status.dependencies.find((d) => d.name === "database") : undefined;

  return (
    <main className="shell">
      <header className="header">
        <div>
          <p className="eyebrow">Gözleme · AI capability registry</p>
          <h1>System status</h1>
        </div>
        <div role="status" aria-live="polite" aria-atomic="true" className="overall">
          <span className="visually-hidden">Overall status: </span>
          <Badge tone={o.tone} testId="overall-status">{o.label}</Badge>
        </div>
      </header>

      <div className="grid">
        <section className="card" aria-labelledby="api-heading">
          <h2 id="api-heading">API service</h2>
          <dl>
            <Row
              label="Status"
              value={
                <Badge tone={!result ? "unknown" : result.reachable ? "ok" : "down"} testId="api-status">
                  {!result ? "Checking…" : result.reachable ? "Reachable" : "Unreachable"}
                </Badge>
              }
            />
            {result && !result.reachable && <Row label="Detail" value={reasonText[result.reason]} testId="api-detail" />}
            {result?.reachable && (
              <>
                <Row label="Service" value={result.status.service} testId="api-service" />
                <Row label="Version" value={<code>{result.status.version}</code>} testId="api-version" />
                <Row label="Contract" value={`v${result.status.contractVersion}`} />
              </>
            )}
          </dl>
        </section>
        <DatabaseCard db={db} apiReachable={!!result?.reachable} />
      </div>

      <footer className="footer">
        <p data-testid="last-checked">
          {result ? <>Last checked {formatTime(result.checkedAt)}</> : "Not checked yet"} · refreshes every{" "}
          {Math.round(pollIntervalMs / 1000)} s
        </p>
        <button type="button" onClick={refresh} className="button">
          Check now
        </button>
      </footer>
      <p className="note">
        Foundation build (GOZ-0000): status only. Identity, organizations and the capability catalog arrive in later
        specs.
      </p>
    </main>
  );
}
