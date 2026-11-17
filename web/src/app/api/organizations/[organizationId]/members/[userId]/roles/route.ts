import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a role change to `PUT …/members/{userId}/roles` (GOZ-0004). */
export async function PUT(request: Request, { params }: { params: Promise<{ organizationId: string; userId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, userId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/members/${segment(userId)}/roles`);
}
