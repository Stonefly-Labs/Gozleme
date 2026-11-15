export const dynamic = "force-dynamic";

/** Anonymous web liveness for container and orchestrator probes (FR-022); depends on nothing downstream. */
export function GET() {
  return Response.json({ status: "live" }, { headers: { "cache-control": "no-store" } });
}
