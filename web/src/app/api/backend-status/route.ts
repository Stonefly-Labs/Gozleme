import { apiBaseUrl, apiHeaders } from "@/lib/api";
import { fetchBackendStatus } from "@/lib/status";

export const dynamic = "force-dynamic";

/**
 * Same-origin proxy for the API status document so the browser never talks to the API directly.
 * Since GOZ-0001 it forwards the session cookie; without a valid session it answers 401 with no
 * status data, and while the database is down (session unverifiable) 503 with no status data.
 */
export async function GET(request: Request) {
  const result = await fetchBackendStatus(apiBaseUrl(), fetch, undefined, undefined, apiHeaders(request, false));
  const status = "session" in result ? (result.session === "expired" ? 401 : 503) : 200;
  return Response.json(result, { status, headers: { "cache-control": "no-store" } });
}
