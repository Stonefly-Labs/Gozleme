import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards to `POST /api/v1/organizations/{id}/select` (context preference). */
export async function POST(request: Request, { params }: { params: Promise<{ organizationId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/select`);
}
