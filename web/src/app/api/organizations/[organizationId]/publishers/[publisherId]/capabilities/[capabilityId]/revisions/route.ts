import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { apiRevisionsPath, cursor } from "@/lib/revisions-server";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }> };

/** Same-origin only; forwards the envelope unchanged to `POST …/capabilities/{capabilityId}/revisions` (GOZ-0007). */
export async function POST(request: Request, { params }: Params): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, publisherId, capabilityId } = await params;
  return forwardToApi(request, apiRevisionsPath(organizationId, publisherId, capabilityId));
}

/** Forwards to `GET …/capabilities/{capabilityId}/revisions`, passing only integer `after` and `limit`. */
export async function GET(request: Request, { params }: Params): Promise<Response> {
  const { organizationId, publisherId, capabilityId } = await params;
  const search = new URL(request.url).searchParams;
  const query = new URLSearchParams();
  const after = cursor(search.get("after") ?? undefined);
  const limit = cursor(search.get("limit") ?? undefined);
  if (after !== null) query.set("after", after);
  if (limit !== null) query.set("limit", limit);
  const suffix = query.size > 0 ? `?${query}` : "";
  return forwardToApi(request, `${apiRevisionsPath(organizationId, publisherId, capabilityId)}${suffix}`);
}
