import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { capabilitiesPath } from "@/lib/publishers-server";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a creation to `POST …/publishers/{publisherId}/capabilities` (GOZ-0005). */
export async function POST(request: Request, { params }: { params: Promise<{ organizationId: string; publisherId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, publisherId } = await params;
  return forwardToApi(request, capabilitiesPath(organizationId, publisherId));
}
