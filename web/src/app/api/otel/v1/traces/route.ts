import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";

export const dynamic = "force-dynamic";

/** Browser span batches are small; anything larger is rejected to bound abuse of this public route. */
export const MAX_BODY_BYTES = 256 * 1024;
const ALLOWED_TYPES = ["application/json", "application/x-protobuf"];
const FORWARD_TIMEOUT_MS = 3000;
/** Per-client token bucket (IF-20): browsers batch spans every second or so; this leaves ample headroom. */
export const RATE_PER_MINUTE = 60;
const MAX_TRACKED_CLIENTS = 10_000;
const buckets = new Map<string, { tokens: number; at: number }>();

/**
 * Client key is the first `x-forwarded-for` entry, as reported by the trusted ingress or Next.js
 * itself. A client-supplied header is only as trustworthy as the deployment's proxy (research R6).
 */
export function allow(request: Request, now = Date.now()): boolean {
  const key = request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() || "unknown";
  const bucket = buckets.get(key) ?? { tokens: RATE_PER_MINUTE, at: now };
  bucket.tokens = Math.min(RATE_PER_MINUTE, bucket.tokens + ((now - bucket.at) / 60_000) * RATE_PER_MINUTE);
  bucket.at = now;
  if (!buckets.has(key) && buckets.size >= MAX_TRACKED_CLIENTS) buckets.clear(); // bounded memory
  buckets.set(key, bucket);
  if (bucket.tokens < 1) return false;
  bucket.tokens -= 1;
  return true;
}

export function resetRateLimits() {
  buckets.clear();
}

/**
 * Same-origin OTLP/HTTP trace ingestion for browser telemetry, forwarded to the collector.
 * Accepts only bounded trace payloads; never echoes bodies; telemetry failures never surface as
 * user-facing errors. It stays anonymous because the sign-in and registration pages are
 * instrumented too (GOZ-0001, IF-20), but accepts only same-origin, rate-limited submissions.
 */
export async function POST(request: Request): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  if (!allow(request)) return new Response(null, { status: 429, headers: { "retry-after": "60" } });
  const contentType = (request.headers.get("content-type") ?? "").split(";")[0]!.trim().toLowerCase();
  if (!ALLOWED_TYPES.includes(contentType)) return new Response(null, { status: 415 });

  const declared = Number(request.headers.get("content-length") ?? "0");
  if (declared > MAX_BODY_BYTES) return new Response(null, { status: 413 });
  const body = await request.arrayBuffer();
  if (body.byteLength > MAX_BODY_BYTES) return new Response(null, { status: 413 });

  const endpoint = process.env.OTEL_EXPORTER_OTLP_ENDPOINT;
  if (!endpoint) return new Response(null, { status: 204 }); // export disabled: accept and drop

  try {
    // Append (not resolve) so endpoints with a path prefix, e.g. https://host/otlp, are preserved (IF-24).
    const upstream = await fetch(`${endpoint.replace(/\/+$/, "")}/v1/traces`, {
      method: "POST",
      headers: { "content-type": contentType },
      body,
      signal: AbortSignal.timeout(FORWARD_TIMEOUT_MS),
    });
    return new Response(null, { status: upstream.ok ? 202 : 502 });
  } catch {
    return new Response(null, { status: 502 });
  }
}
