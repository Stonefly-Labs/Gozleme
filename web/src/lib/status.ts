/** Types and fetch logic for the API status contract v1 (`/api/v1/status`). */

export type OverallStatus = "healthy" | "degraded" | "unavailable";
export type DependencyHealth = "healthy" | "unavailable";

export interface VectorCapability {
  available: boolean;
  enabled: boolean;
}

export interface DependencyStatus {
  name: string;
  status: DependencyHealth;
  required: boolean;
  latencyMs: number | null;
  schemaVersion: string | null;
  message: string | null;
  capabilities: { vector?: VectorCapability } | null;
}

export interface ServiceStatus {
  contractVersion: "1";
  service: string;
  version: string;
  status: OverallStatus;
  checkedAt: string;
  dependencies: DependencyStatus[];
}

export type UnreachableReason = "timeout" | "unreachable" | "invalid-response";

/**
 * Why no status data is shown although the API answered (GOZ-0001): `expired` — no valid session;
 * `unverifiable` — the database is down, so the session cannot be verified (fail closed, FR-021a).
 */
export type SessionProblem = "expired" | "unverifiable";

/** What the dashboard receives from its same-origin route. */
export type BackendStatusResult =
  | { reachable: true; checkedAt: string; status: ServiceStatus }
  | { reachable: true; checkedAt: string; session: SessionProblem }
  | { reachable: false; checkedAt: string; reason: UnreachableReason };

export const API_TIMEOUT_MS = 3000;

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null;
const isNullableString = (v: unknown) => v === null || typeof v === "string";

function isDependency(v: unknown): v is DependencyStatus {
  if (!isObject(v)) return false;
  const caps = v.capabilities;
  // Contract v1: `capabilities` is null or an object whose `vector` entry is optional.
  const capsOk =
    caps === null ||
    (isObject(caps) &&
      (caps.vector === undefined ||
        (isObject(caps.vector) &&
          typeof caps.vector.available === "boolean" &&
          typeof caps.vector.enabled === "boolean")));
  return (
    typeof v.name === "string" &&
    (v.status === "healthy" || v.status === "unavailable") &&
    typeof v.required === "boolean" &&
    (v.latencyMs === null || typeof v.latencyMs === "number") &&
    isNullableString(v.schemaVersion) &&
    isNullableString(v.message) &&
    capsOk
  );
}

async function isUnavailable(res: Response) {
  try {
    const body: unknown = await res.json();
    return isObject(body) && body.error === "unavailable";
  } catch {
    return false;
  }
}

/** Structural validation; anything unexpected is treated as an invalid response, never rendered. */
export function parseServiceStatus(v: unknown): ServiceStatus | null {
  if (!isObject(v)) return null;
  const ok =
    v.contractVersion === "1" &&
    typeof v.service === "string" &&
    typeof v.version === "string" &&
    (v.status === "healthy" || v.status === "degraded" || v.status === "unavailable") &&
    typeof v.checkedAt === "string" &&
    Array.isArray(v.dependencies) &&
    v.dependencies.every(isDependency);
  return ok ? (v as unknown as ServiceStatus) : null;
}

/**
 * Server-side call from the web tier to the API, bounded by a timeout. `headers` carries the
 * forwarded session cookie (the status endpoint requires a session since GOZ-0001).
 */
export async function fetchBackendStatus(
  apiBaseUrl: string,
  fetchImpl: typeof fetch = fetch,
  timeoutMs = API_TIMEOUT_MS,
  now: () => Date = () => new Date(),
  headers: HeadersInit = { accept: "application/json" },
): Promise<BackendStatusResult> {
  const checkedAt = () => now().toISOString();
  try {
    const res = await fetchImpl(new URL("/api/v1/status", apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers,
    });
    if (res.status === 401) return { reachable: true, checkedAt: checkedAt(), session: "expired" };
    if (res.status === 503 && (await isUnavailable(res))) return { reachable: true, checkedAt: checkedAt(), session: "unverifiable" };
    if (!res.ok) return { reachable: false, checkedAt: checkedAt(), reason: "invalid-response" };
    const status = parseServiceStatus(await res.json());
    return status
      ? { reachable: true, checkedAt: checkedAt(), status }
      : { reachable: false, checkedAt: checkedAt(), reason: "invalid-response" };
  } catch (err) {
    // Match by name: DOMException identity differs across runtimes (Node, jsdom, edge).
    const name = isObject(err) ? err.name : undefined;
    const timedOut = name === "TimeoutError" || name === "AbortError";
    if (err instanceof SyntaxError) return { reachable: false, checkedAt: checkedAt(), reason: "invalid-response" };
    return { reachable: false, checkedAt: checkedAt(), reason: timedOut ? "timeout" : "unreachable" };
  }
}
