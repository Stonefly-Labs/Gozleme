import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a removal (or leave, for oneself) to `DELETE …/members/{userId}`. */
export async function DELETE(request: Request, { params }: { params: Promise<{ organizationId: string; userId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, userId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/members/${segment(userId)}`);
}
