import { forwardToApi } from "@/lib/api";
import { segment } from "@/lib/organizations";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { capabilitiesPath } from "@/lib/publishers-server";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string; publisherId: string; capabilityId: string }> };

/** Same-origin only; forwards the envelope unchanged to `POST …/capabilities/{capabilityId}/manifests/validate` (GOZ-0006). */
export async function POST(request: Request, { params }: Params): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, publisherId, capabilityId } = await params;
  return forwardToApi(request, `${capabilitiesPath(organizationId, publisherId)}/${segment(capabilityId)}/manifests/validate`);
}
