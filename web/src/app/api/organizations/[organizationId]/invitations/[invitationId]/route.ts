import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Same-origin only; forwards a revocation to `DELETE …/invitations/{invitationId}`. */
export async function DELETE(request: Request, { params }: { params: Promise<{ organizationId: string; invitationId: string }> }): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId, invitationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/invitations/${segment(invitationId)}`);
}
