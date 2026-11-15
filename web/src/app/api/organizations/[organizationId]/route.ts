import { forwardToApi } from "@/lib/api";
import { crossSiteForbidden, isSameOrigin } from "@/lib/origin";
import { segment } from "@/lib/organizations";

export const dynamic = "force-dynamic";

type Params = { params: Promise<{ organizationId: string }> };

/** Forwards to `GET /api/v1/organizations/{id}`. */
export async function GET(request: Request, { params }: Params): Promise<Response> {
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}`);
}

/** Same-origin only; forwards a rename to `PATCH /api/v1/organizations/{id}`. */
export async function PATCH(request: Request, { params }: Params): Promise<Response> {
  if (!isSameOrigin(request)) return crossSiteForbidden();
  const { organizationId } = await params;
  return forwardToApi(request, `/api/v1/organizations/${segment(organizationId)}`);
}
