import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { publisherPath } from "@/lib/publishers-server";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a display-name change to `PATCH …/publishers/{publisherId}` (GOZ-0005). */
export async function PATCH(request: Request, { params }: { params: Promise<{ organizationId: string; publisherId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, publisherId } = await params;
  return forwardToApi(request, publisherPath(organizationId, publisherId));
}
