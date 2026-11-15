/**
 * CSRF layer 1 (research R5): unsafe browser-facing routes accept only same-origin requests, judged
 * by Fetch Metadata (`Sec-Fetch-Site`) or, when absent, an `Origin` equal to this site's origin.
 * Requests carrying neither are refused: browsers always send one of them on POST.
 */
export function expectedOrigin(request: Pick<Request, "headers" | "url">): string {
  const configured = process.env.GOZLEME_PUBLIC_ORIGIN;
  if (configured) return new URL(configured).origin;
  const host = request.headers.get("x-forwarded-host") ?? request.headers.get("host");
  const proto = request.headers.get("x-forwarded-proto") ?? new URL(request.url).protocol.replace(":", "");
  return host ? `${proto}://${host}` : new URL(request.url).origin;
}

export function isSameOrigin(request: Pick<Request, "headers" | "url">): boolean {
  const site = request.headers.get("sec-fetch-site");
  if (site) return site === "same-origin";
  const origin = request.headers.get("origin");
  return !!origin && origin !== "null" && origin === expectedOrigin(request);
}

export const crossSiteForbidden = () =>
  Response.json({ error: "forbidden" }, { status: 403, headers: { "cache-control": "no-store" } });
