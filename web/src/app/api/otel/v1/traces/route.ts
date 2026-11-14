export const dynamic = "force-dynamic";

/** Browser span batches are small; anything larger is rejected to bound abuse of this public route. */
export const MAX_BODY_BYTES = 256 * 1024;
const ALLOWED_TYPES = ["application/json", "application/x-protobuf"];
const FORWARD_TIMEOUT_MS = 3000;

/**
 * Same-origin OTLP/HTTP trace ingestion for browser telemetry, forwarded to the collector.
 * Accepts only bounded trace payloads; never echoes bodies; telemetry failures never surface as
 * user-facing errors. This route is unauthenticated until identity exists (GOZ-0001).
 */
export async function POST(request: Request): Promise<Response> {
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
