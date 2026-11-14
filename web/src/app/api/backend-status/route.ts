import { fetchBackendStatus } from "@/lib/status";

export const dynamic = "force-dynamic";

/** Same-origin proxy for the API status document so the browser never talks to the API directly. */
export async function GET() {
  const apiUrl = process.env.GOZLEME_API_URL ?? "http://localhost:8080";
  const result = await fetchBackendStatus(apiUrl);
  return Response.json(result, { headers: { "cache-control": "no-store" } });
}
