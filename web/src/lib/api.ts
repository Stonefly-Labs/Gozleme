/**
 * Server-side forwarding from same-origin route handlers to the API (research R4). The browser never
 * talks to the API: the session cookie, client address and the `x-gozleme-client` CSRF header are
 * forwarded, and the API's `set-cookie` headers are relayed back unchanged.
 */

export const API_FORWARD_TIMEOUT_MS = 5000;
export const WEB_CLIENT_HEADER = "x-gozleme-client";

export function apiBaseUrl() {
  return process.env.GOZLEME_API_URL ?? "http://localhost:8080";
}

export function apiHeaders(request: Pick<Request, "headers">, hasBody: boolean): Headers {
  const headers = new Headers({ accept: "application/json", [WEB_CLIENT_HEADER]: "web" });
  if (hasBody) headers.set("content-type", "application/json");
  const cookie = request.headers.get("cookie");
  if (cookie) headers.set("cookie", cookie);
  const forwardedFor = request.headers.get("x-forwarded-for");
  if (forwardedFor) headers.set("x-forwarded-for", forwardedFor);
  return headers;
}

export const unavailable = () =>
  Response.json({ error: "unavailable" }, { status: 503, headers: { "cache-control": "no-store" } });

/** Forward one call; network failures and timeouts become a generic 503 with no internals. */
export async function forwardToApi(
  request: Request,
  path: string,
  { method = request.method, fetchImpl = fetch }: { method?: string; fetchImpl?: typeof fetch } = {},
): Promise<Response> {
  const body = method === "GET" ? undefined : await request.text();
  let upstream: Response;
  try {
    upstream = await fetchImpl(new URL(path, apiBaseUrl()), {
      method,
      headers: apiHeaders(request, !!body),
      body: body || undefined,
      cache: "no-store",
      redirect: "manual",
      signal: AbortSignal.timeout(API_FORWARD_TIMEOUT_MS),
    });
  } catch {
    return unavailable();
  }

  const headers = new Headers({ "cache-control": "no-store" });
  for (const cookie of upstream.headers.getSetCookie()) headers.append("set-cookie", cookie);
  const type = upstream.headers.get("content-type");
  if (type) headers.set("content-type", type);
  const status = upstream.status >= 500 ? 503 : upstream.status;
  return new Response(status === 204 ? null : await upstream.text(), { status, headers });
}
