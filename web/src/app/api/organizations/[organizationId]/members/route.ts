import { forwardToApi } from "@/lib/api";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

/** Forwards to `GET /api/v1/organizations/{id}/members`. */
export async function GET(request: Request, { params }: { params: Promise<{ organizationId: string }> }): Promise<Response> {
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}/members`);
}
